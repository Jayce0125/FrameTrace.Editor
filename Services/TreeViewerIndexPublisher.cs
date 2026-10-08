using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using FrameTrace.Editor.Domain;

namespace FrameTrace.Editor.Services;

public sealed class TreeViewerIndexPublisher
{
    private const string IndexVariable = "window.VIEWER_INDEX =";
    private const string CategoriesVariable = "window.VIEWER_CATEGORIES =";
    private const string OrderVariable = "window.VIEWER_ORDER =";
    private const string DataVariable = "window.VIEWER_CATEGORY_DATA";
    private const string RecordVariable = "window.FRAME_TRACE_RECORD =";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = new SnakeCaseNamingPolicy(), WriteIndented = true };

    public void Publish(string assetsDirectory, string webViewerDirectory, string projectName, int pageSize)
    {
        var dataDirectory = Path.Combine(webViewerDirectory, "data"); Directory.CreateDirectory(dataDirectory);
        var existingCategories = ReadCategories(Path.Combine(dataDirectory, "categories.js"));
        var order = ViewerOrderStore.Read(webViewerDirectory);
        var records = ReadRecords(assetsDirectory, webViewerDirectory, out var legacyAssetOrder);
        var originalCategoryIds = records.ToDictionary(record => record.Id, record => record.CategoryId, StringComparer.OrdinalIgnoreCase);
        var categories = BuildCategories(records, existingCategories, order, legacyAssetOrder);
        SynchronizeRecordCategoryIds(records, originalCategoryIds);
        var stagingDirectory = Path.Combine(dataDirectory, $".publish-{Guid.NewGuid():N}"); Directory.CreateDirectory(stagingDirectory);
        var version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(); order.Version = version;
        var pages = new Dictionary<string, CategoryPages>();
        foreach (var category in categories)
        {
            var byId = records.Where(record => record.CategoryId == category.Id).ToDictionary(record => record.Id, StringComparer.OrdinalIgnoreCase);
            var ids = ViewerOrderStore.Assets(order, category.Id); var categoryRecords = ids.Where(byId.ContainsKey).Select(id => byId[id]).Concat(byId.Values.Where(record => !ids.Contains(record.Id, StringComparer.OrdinalIgnoreCase)).OrderBy(record => record.Id, StringComparer.Ordinal)).ToArray();
            ViewerOrderStore.SetAssets(order, category.Id, categoryRecords.Select(record => record.Id));
            if (categoryRecords.Length == 0) continue;
            var directoryName = $"c{category.Id[..8]}"; var directory = Path.Combine(stagingDirectory, directoryName); Directory.CreateDirectory(directory); var pageNames = new List<string>();
            foreach (var (chunk, page) in categoryRecords.Chunk(Math.Max(1, pageSize)).Select((chunk, page) => (chunk, page)))
            {
                var key = $"{category.Id}__{page}"; WriteAtomically(Path.Combine(directory, $"{page}.js"), $"{DataVariable} = {DataVariable} || {{}};{Environment.NewLine}{DataVariable}[{JsonSerializer.Serialize(key)}] = {JsonSerializer.Serialize(new CategoryData(category.Id, page, chunk), JsonOptions)};{Environment.NewLine}"); pageNames.Add($"data/{directoryName}/{page}.js?v={version}");
            }
            pages[category.Id] = new CategoryPages(directoryName, pageNames, categoryRecords.Length);
        }
        var rootIds = Complete(ViewerOrderStore.Siblings(order, null), categories.Where(category => category.ParentId is null).Select(category => category.Id)); ViewerOrderStore.SetSiblings(order, null, rootIds);
        foreach (var category in categories) ViewerOrderStore.SetSiblings(order, category.Id, Complete(ViewerOrderStore.Siblings(order, category.Id), categories.Where(child => child.ParentId == category.Id).Select(child => child.Id)));
        var index = new TreeIndex(version, projectName, records.Count, pages);
        WriteAtomically(Path.Combine(stagingDirectory, "categories.js"), $"{CategoriesVariable} {JsonSerializer.Serialize(new CategoryDocument(version, categories), JsonOptions)};{Environment.NewLine}");
        WriteAtomically(Path.Combine(stagingDirectory, "order.js"), $"{OrderVariable} {JsonSerializer.Serialize(order, JsonOptions)};{Environment.NewLine}");
        WriteAtomically(Path.Combine(stagingDirectory, "index.js"), $"{IndexVariable} {JsonSerializer.Serialize(index, JsonOptions)};{Environment.NewLine}");
        CommitStaging(dataDirectory, stagingDirectory);
        SynchronizeOutputViewer(webViewerDirectory);
    }

    private static List<CategoryNode> BuildCategories(IReadOnlyList<AssetRecord> records, IReadOnlyList<CategoryNode> existing, ViewerOrderDocument order, IReadOnlyDictionary<string, int> legacyAssetOrder)
    {
        var result = new List<CategoryNode>(); var byPath = new Dictionary<string, CategoryNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var rootId in Complete(ViewerOrderStore.Siblings(order, null), existing.Where(node => node.ParentId is null).Select(node => node.Id))) { var root = existing.FirstOrDefault(node => node.Id == rootId); if (root is not null) AddCategoryPath(root, "", existing, result, byPath, order); }
        foreach (var record in records)
        {
            string? parentId = null; var path = string.Empty;
            foreach (var name in (record.FolderName ?? string.Empty).Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                path = path.Length == 0 ? name : $"{path}/{name}";
                if (!byPath.TryGetValue(path, out var node)) { node = new CategoryNode(CreateStableId(path), name, parentId, 0, 0); result.Add(node); byPath[path] = node; }
                parentId = node.Id;
            }
            if (parentId is not null) record.CategoryId = parentId;
        }
        foreach (var category in result) { category.Count = records.Count(record => record.CategoryId == category.Id); category.TotalCount = records.Count(record => record.CategoryId == category.Id || IsDescendant(record.CategoryId, category.Id, result)); }
        result.RemoveAll(category => category.TotalCount == 0);
        foreach (var group in records.GroupBy(record => record.CategoryId, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(group.Key) || !result.Any(node => node.Id == group.Key)) continue;
            var existingIds = ViewerOrderStore.Assets(order, group.Key); var ids = existingIds.Where(id => group.Any(record => string.Equals(record.Id, id, StringComparison.OrdinalIgnoreCase))).Concat(group.Where(record => !existingIds.Contains(record.Id, StringComparer.OrdinalIgnoreCase)).OrderBy(record => legacyAssetOrder.TryGetValue(record.Id, out var value) ? value : int.MaxValue).ThenBy(record => record.Id, StringComparer.Ordinal).Select(record => record.Id)); ViewerOrderStore.SetAssets(order, group.Key, ids);
        }
        return result;
    }

    private static void AddCategoryPath(CategoryNode node, string parentPath, IReadOnlyList<CategoryNode> all, List<CategoryNode> result, Dictionary<string, CategoryNode> byPath, ViewerOrderDocument order)
    {
        var path = parentPath.Length == 0 ? node.Name : $"{parentPath}/{node.Name}"; byPath[path] = node; result.Add(node);
        foreach (var childId in Complete(ViewerOrderStore.Siblings(order, node.Id), all.Where(child => child.ParentId == node.Id).Select(child => child.Id))) { var child = all.FirstOrDefault(item => item.Id == childId); if (child is not null) AddCategoryPath(child, path, all, result, byPath, order); }
    }
    private static bool IsDescendant(string? categoryId, string ancestorId, IReadOnlyList<CategoryNode> categories) { var current = categories.FirstOrDefault(category => category.Id == categoryId); while (current?.ParentId is not null) { if (current.ParentId == ancestorId) return true; current = categories.FirstOrDefault(category => category.Id == current.ParentId); } return false; }
    private static List<string> Complete(IEnumerable<string> preferred, IEnumerable<string> available) { var result = preferred.ToList(); result.AddRange(available.Where(id => !result.Contains(id, StringComparer.OrdinalIgnoreCase))); return result; }

    private static List<AssetRecord> ReadRecords(string assetsDirectory, string webViewerDirectory, out Dictionary<string, int> legacyOrder)
    {
        legacyOrder = new(StringComparer.OrdinalIgnoreCase); var records = new List<AssetRecord>(); if (!Directory.Exists(assetsDirectory)) return records;
        foreach (var path in Directory.EnumerateFiles(assetsDirectory, "record.js", SearchOption.AllDirectories)) try
        {
            var script = File.ReadAllText(path); const string marker = "window.FRAME_TRACE_RECORD ="; var start = script.IndexOf(marker, StringComparison.Ordinal); if (start < 0) continue; var json = script[(start + marker.Length)..].Trim().TrimEnd(';'); var record = JsonSerializer.Deserialize<AssetRecord>(json, JsonOptions); if (record is null) continue;
            using (var document = JsonDocument.Parse(json)) if (document.RootElement.TryGetProperty("order", out var value) && value.TryGetInt32(out var legacy)) legacyOrder[record.Id] = legacy;
            var directory = Path.GetDirectoryName(path)!; var prefix = Path.GetRelativePath(webViewerDirectory, directory).Replace(Path.DirectorySeparatorChar, '/') + "/"; var assetDirectoryName = Path.GetFileName(directory);
            records.Add(new AssetRecord { Id = record.Id, DisplayName = record.DisplayName, MediaType = record.MediaType, LocalPath = prefix + NormalizeRecordPath(record.LocalPath, assetDirectoryName), FolderName = record.FolderName, PosterPath = record.PosterPath is null ? null : prefix + NormalizeRecordPath(record.PosterPath, assetDirectoryName), Prompt = record.Prompt, ContentHash = record.ContentHash, References = record.References.Select(reference => new ReferenceResource { Type = reference.Type, LocalPath = prefix + NormalizeRecordPath(reference.LocalPath, assetDirectoryName) }).ToArray(), Meta = record.Meta, Author = record.Author, Tags = record.Tags, Rating = record.Rating, ReviewStatus = record.ReviewStatus, CategoryId = record.CategoryId, SourcePath = path });
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { }
        return records;
    }
    private static string NormalizeRecordPath(string path, string assetDirectoryName) { var normalized = path.Replace('\\', '/'); var prefix = $"../assets/{assetDirectoryName}/"; while (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) normalized = normalized[prefix.Length..]; return normalized; }
    private static IReadOnlyList<CategoryNode> ReadCategories(string path) { if (!File.Exists(path)) return []; try { var script = File.ReadAllText(path); var start = script.IndexOf(CategoriesVariable, StringComparison.Ordinal); return start < 0 ? [] : JsonSerializer.Deserialize<CategoryDocument>(script[(start + CategoriesVariable.Length)..].Trim().TrimEnd(';'), JsonOptions)?.Nodes ?? []; } catch (Exception exception) when (exception is IOException or JsonException) { return []; } }
    private static void CommitStaging(string dataDirectory, string stagingDirectory) { var backupDirectory = Path.Combine(dataDirectory, $".backup-{Guid.NewGuid():N}"); Directory.CreateDirectory(backupDirectory); try { foreach (var name in new[] { "categories.js", "order.js", "index.js" }) { var path = Path.Combine(dataDirectory, name); if (File.Exists(path)) File.Copy(path, Path.Combine(backupDirectory, name)); } foreach (var directory in Directory.EnumerateDirectories(dataDirectory).Where(directory => Path.GetFileName(directory).StartsWith("c", StringComparison.OrdinalIgnoreCase))) Directory.Move(directory, Path.Combine(backupDirectory, Path.GetFileName(directory))); foreach (var directory in Directory.EnumerateDirectories(stagingDirectory).Where(directory => Path.GetFileName(directory).StartsWith("c", StringComparison.OrdinalIgnoreCase))) Directory.Move(directory, Path.Combine(dataDirectory, Path.GetFileName(directory))); foreach (var name in new[] { "categories.js", "order.js", "index.js" }) File.Move(Path.Combine(stagingDirectory, name), Path.Combine(dataDirectory, name), true); } catch { foreach (var directory in Directory.EnumerateDirectories(backupDirectory).Where(directory => Path.GetFileName(directory).StartsWith("c", StringComparison.OrdinalIgnoreCase))) { var restore = Path.Combine(dataDirectory, Path.GetFileName(directory)); if (Directory.Exists(restore)) Directory.Delete(restore, true); Directory.Move(directory, restore); } foreach (var name in new[] { "categories.js", "order.js", "index.js" }) { var backup = Path.Combine(backupDirectory, name); if (File.Exists(backup)) File.Copy(backup, Path.Combine(dataDirectory, name), true); } throw; } finally { if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, true); if (Directory.Exists(backupDirectory)) Directory.Delete(backupDirectory, true); } }
    internal static string CreateStableId(string value) { var bytes = MD5.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant())); bytes[6] = (byte)((bytes[6] & 0x0f) | 0x30); bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80); return new Guid(bytes).ToString(); }
    private static void SynchronizeOutputViewer(string webViewerDirectory)
    {
        var publishedDirectory = new DirectoryInfo(webViewerDirectory);
        var formalDirectory = publishedDirectory.Parent;
        var rootDirectory = formalDirectory?.Parent;
        if (!string.Equals(formalDirectory?.Name, "正式网页", StringComparison.OrdinalIgnoreCase) || rootDirectory is null) return;
        var outputViewer = Path.Combine(rootDirectory.FullName, "output", "webviewer");
        if (!Directory.Exists(outputViewer)) return;
        Directory.Delete(outputViewer, true);
        foreach (var source in Directory.EnumerateFiles(webViewerDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(webViewerDirectory, source);
            var target = Path.Combine(outputViewer, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, true);
        }
    }
    private static void WriteAtomically(string path, string content) { var temporary = $"{path}.{Guid.NewGuid():N}.tmp"; File.WriteAllText(temporary, content, new UTF8Encoding(false)); File.Move(temporary, path, true); }

    // 发布时 BuildCategories 会按 folder_name 重建 CategoryId，但不回写 record.js 会导致两边分叉：
    // 拖拽排序等操作读 Record.CategoryId 作为 order.js 的键，键错了排序就会被静默丢弃。
    private static void SynchronizeRecordCategoryIds(IReadOnlyList<AssetRecord> records, IReadOnlyDictionary<string, string> originalCategoryIds)
    {
        foreach (var record in records)
        {
            if (record.SourcePath is null) continue;
            if (originalCategoryIds.TryGetValue(record.Id, out var original) && string.Equals(original, record.CategoryId, StringComparison.OrdinalIgnoreCase)) continue;
            try { PatchRecordCategoryId(record.SourcePath, record.CategoryId); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { }
        }
    }

    private static void PatchRecordCategoryId(string sourcePath, string categoryId)
    {
        var script = File.ReadAllText(sourcePath);
        var start = script.IndexOf(RecordVariable, StringComparison.Ordinal);
        if (start < 0) return;
        var record = JsonSerializer.Deserialize<AssetRecord>(script[(start + RecordVariable.Length)..].Trim().TrimEnd(';'), JsonOptions);
        if (record is null || string.Equals(record.CategoryId, categoryId, StringComparison.OrdinalIgnoreCase)) return;
        record.CategoryId = categoryId;
        WriteAtomically(sourcePath, $"{RecordVariable} {JsonSerializer.Serialize(record, JsonOptions)};{Environment.NewLine}");
    }
    private sealed record CategoryDocument(string Version, IReadOnlyList<CategoryNode> Nodes); private sealed record TreeIndex(string Version, string ProjectName, int TotalAssets, IReadOnlyDictionary<string, CategoryPages> CategoryPages); private sealed record CategoryPages(string Dir, IReadOnlyList<string> Pages, int RecordCount); private sealed record CategoryData(string CategoryId, int Page, IReadOnlyList<AssetRecord> Records);
    private sealed class CategoryNode { public CategoryNode(string id, string name, string? parentId, int count, int totalCount) { Id = id; Name = name; ParentId = parentId; Count = count; TotalCount = totalCount; } public string Id { get; set; } public string Name { get; set; } public string? ParentId { get; set; } public int Count { get; set; } public int TotalCount { get; set; } }
}
