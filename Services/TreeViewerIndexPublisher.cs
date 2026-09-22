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
    private const string DataVariable = "window.VIEWER_CATEGORY_DATA";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = new SnakeCaseNamingPolicy(), WriteIndented = true };

    public void Publish(string assetsDirectory, string webViewerDirectory, string projectName, int pageSize)
    {
        var dataDirectory = Path.Combine(webViewerDirectory, "data");
        Directory.CreateDirectory(dataDirectory);
        var existingCategories = ReadCategories(Path.Combine(dataDirectory, "categories.js"));
        var stagingDirectory = Path.Combine(dataDirectory, $".publish-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);

        var version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var records = ReadRecords(assetsDirectory, webViewerDirectory).ToArray();
        var categories = BuildCategories(records, existingCategories);
        foreach (var record in records) PersistRecord(record);
        var pages = new Dictionary<string, CategoryPages>();
        foreach (var category in categories)
        {
            var categoryRecords = records.Where(record => record.CategoryId == category.Id).OrderBy(record => record.Order).ToArray();
            if (categoryRecords.Length == 0) continue;
            var directoryName = $"c{category.Id[..8]}";
            var directory = Path.Combine(stagingDirectory, directoryName);
            Directory.CreateDirectory(directory);
            var pageNames = new List<string>();
            var chunks = categoryRecords.Chunk(Math.Max(1, pageSize)).ToArray();
            for (var page = 0; page < chunks.Length; page++)
            {
                var fileName = $"{page}.js";
                var key = $"{category.Id}__{page}";
                var document = new CategoryData(category.Id, page, chunks[page]);
                WriteAtomically(Path.Combine(directory, fileName), $"{DataVariable} = {DataVariable} || {{}};{Environment.NewLine}{DataVariable}[{JsonSerializer.Serialize(key)}] = {JsonSerializer.Serialize(document, JsonOptions)};{Environment.NewLine}");
                pageNames.Add($"data/{directoryName}/{fileName}?v={version}");
            }
            pages[category.Id] = new CategoryPages(directoryName, pageNames, categoryRecords.Length);
        }

        var rootIds = categories.Where(category => category.ParentId is null).OrderBy(category => category.Order).Select(category => category.Id).ToArray();
        var index = new TreeIndex(version, projectName, records.Length, rootIds, pages);
        var categoryDocument = new CategoryDocument(version, categories);
        WriteAtomically(Path.Combine(stagingDirectory, "categories.js"), $"{CategoriesVariable} {JsonSerializer.Serialize(categoryDocument, JsonOptions)};{Environment.NewLine}");
        WriteAtomically(Path.Combine(stagingDirectory, "index.js"), $"{IndexVariable} {JsonSerializer.Serialize(index, JsonOptions)};{Environment.NewLine}");
        CommitStaging(dataDirectory, stagingDirectory);
    }

    private static List<CategoryNode> BuildCategories(IReadOnlyList<AssetRecord> records, IReadOnlyList<CategoryNode> existingCategories)
    {
        var result = new List<CategoryNode>();
        var byPath = new Dictionary<string, CategoryNode>(StringComparer.OrdinalIgnoreCase);
        AddExistingCategories(result, byPath, existingCategories);
        foreach (var record in records)
        {
            var parentId = (string?)null;
            var path = string.Empty;
            foreach (var name in (record.FolderName ?? string.Empty).Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                path = path.Length == 0 ? name : $"{path}/{name}";
                if (byPath.TryGetValue(path, out var existing)) { parentId = existing.Id; continue; }
                var node = new CategoryNode(CreateStableId(path), name, parentId, result.Count(item => item.ParentId == parentId), 0, 0);
                byPath[path] = node; result.Add(node); parentId = node.Id;
            }
            if (parentId is null) continue;
            var category = result.First(item => item.Id == parentId);
            record.CategoryId = category.Id;
        }

        foreach (var category in result)
        {
            category.Count = records.Count(record => record.CategoryId == category.Id);
            category.TotalCount = records.Count(record => record.CategoryId == category.Id || IsDescendant(record.CategoryId, category.Id, result));
        }
        result.RemoveAll(category => category.TotalCount == 0);
        foreach (var group in records.GroupBy(record => record.CategoryId, StringComparer.Ordinal))
        {
            var order = 0;
            foreach (var record in group.OrderBy(record => record.Order).ThenBy(record => record.Id, StringComparer.Ordinal)) record.Order = order++;
        }
        return result;
    }

    private static void AddExistingCategories(List<CategoryNode> result, Dictionary<string, CategoryNode> byPath, IReadOnlyList<CategoryNode> existingCategories)
    {
        foreach (var root in existingCategories.Where(category => category.ParentId is null).OrderBy(category => category.Order))
        {
            AddCategoryPath(root, string.Empty, existingCategories, result, byPath);
        }
    }

    private static void AddCategoryPath(CategoryNode node, string parentPath, IReadOnlyList<CategoryNode> all, List<CategoryNode> result, Dictionary<string, CategoryNode> byPath)
    {
        var path = parentPath.Length == 0 ? node.Name : $"{parentPath}/{node.Name}";
        byPath[path] = node; result.Add(node);
        foreach (var child in all.Where(category => category.ParentId == node.Id).OrderBy(category => category.Order)) AddCategoryPath(child, path, all, result, byPath);
    }

    private static bool IsDescendant(string? categoryId, string ancestorId, IReadOnlyList<CategoryNode> categories)
    {
        var current = categories.FirstOrDefault(category => category.Id == categoryId);
        while (current?.ParentId is not null)
        {
            if (current.ParentId == ancestorId) return true;
            current = categories.FirstOrDefault(category => category.Id == current.ParentId);
        }
        return false;
    }

    private static IEnumerable<AssetRecord> ReadRecords(string assetsDirectory, string webViewerDirectory)
    {
        if (!Directory.Exists(assetsDirectory)) yield break;
        foreach (var path in Directory.EnumerateFiles(assetsDirectory, "record.js", SearchOption.AllDirectories))
        {
            AssetRecord? record = null;
            try
            {
                var script = File.ReadAllText(path); var marker = "window.FRAME_TRACE_RECORD ="; var start = script.IndexOf(marker, StringComparison.Ordinal);
                if (start >= 0) record = JsonSerializer.Deserialize<AssetRecord>(script[(start + marker.Length)..].Trim().TrimEnd(';'), JsonOptions);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { }
            if (record is null) continue;
            var directory = Path.GetDirectoryName(path)!; var prefix = Path.GetRelativePath(webViewerDirectory, directory).Replace(Path.DirectorySeparatorChar, '/') + "/";
            var assetDirectoryName = Path.GetFileName(directory);
            yield return new AssetRecord
            {
                Id = record.Id,
                DisplayName = record.DisplayName,
                MediaType = record.MediaType,
                LocalPath = prefix + NormalizeRecordPath(record.LocalPath, assetDirectoryName),
                FolderName = record.FolderName,
                PosterPath = record.PosterPath is null ? null : prefix + NormalizeRecordPath(record.PosterPath, assetDirectoryName),
                Prompt = record.Prompt,
                ContentHash = record.ContentHash,
                References = record.References.Select(reference => new ReferenceResource
                {
                    Type = reference.Type,
                    LocalPath = prefix + NormalizeRecordPath(reference.LocalPath, assetDirectoryName)
                }).ToArray(),
                Meta = record.Meta,
                Author = record.Author,
                Tags = record.Tags,
                Rating = record.Rating,
                ReviewStatus = record.ReviewStatus,
                CategoryId = record.CategoryId,
                Order = record.Order,
                SourcePath = path
            };
        }
    }

    private static string NormalizeRecordPath(string path, string assetDirectoryName)
    {
        var normalized = path.Replace('\\', '/');
        var repeatedPrefix = $"../assets/{assetDirectoryName}/";
        while (normalized.StartsWith(repeatedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[repeatedPrefix.Length..];
        }

        return normalized;
    }

    private static IReadOnlyList<CategoryNode> ReadCategories(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            var script = File.ReadAllText(path); var marker = CategoriesVariable; var start = script.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return [];
            var json = script[(start + marker.Length)..].Trim().TrimEnd(';');
            return JsonSerializer.Deserialize<CategoryDocument>(json, JsonOptions)?.Nodes ?? [];
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException) { return []; }
    }

    private static void PersistRecord(AssetRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.SourcePath)) return;
        var json = JsonSerializer.Serialize(record, JsonOptions);
        WriteAtomically(record.SourcePath, $"window.FRAME_TRACE_RECORD = {json};{Environment.NewLine}");
    }

    private static void CommitStaging(string dataDirectory, string stagingDirectory)
    {
        var backupDirectory = Path.Combine(dataDirectory, $".backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(backupDirectory);
        try
        {
            foreach (var fileName in new[] { "categories.js", "index.js" })
            {
                var filePath = Path.Combine(dataDirectory, fileName);
                if (File.Exists(filePath)) File.Copy(filePath, Path.Combine(backupDirectory, fileName));
            }
            foreach (var directory in Directory.EnumerateDirectories(dataDirectory).Where(directory => Path.GetFileName(directory).StartsWith("c", StringComparison.OrdinalIgnoreCase)))
            {
                Directory.Move(directory, Path.Combine(backupDirectory, Path.GetFileName(directory)));
            }
            foreach (var directory in Directory.EnumerateDirectories(stagingDirectory).Where(directory => Path.GetFileName(directory).StartsWith("c", StringComparison.OrdinalIgnoreCase)))
            {
                Directory.Move(directory, Path.Combine(dataDirectory, Path.GetFileName(directory)));
            }
            File.Move(Path.Combine(stagingDirectory, "categories.js"), Path.Combine(dataDirectory, "categories.js"), true);
            File.Move(Path.Combine(stagingDirectory, "index.js"), Path.Combine(dataDirectory, "index.js"), true);
        }
        catch
        {
            foreach (var directory in Directory.EnumerateDirectories(backupDirectory).Where(directory => Path.GetFileName(directory).StartsWith("c", StringComparison.OrdinalIgnoreCase)))
            {
                var restoredPath = Path.Combine(dataDirectory, Path.GetFileName(directory));
                if (Directory.Exists(restoredPath)) Directory.Delete(restoredPath, true);
                Directory.Move(directory, restoredPath);
            }
            foreach (var fileName in new[] { "categories.js", "index.js" })
            {
                var backupPath = Path.Combine(backupDirectory, fileName);
                if (File.Exists(backupPath)) File.Copy(backupPath, Path.Combine(dataDirectory, fileName), true);
            }
            throw;
        }
        finally
        {
            if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, true);
            if (Directory.Exists(backupDirectory)) Directory.Delete(backupDirectory, true);
        }
    }

    private static string CreateStableId(string value)
    {
        var bytes = MD5.HashData(System.Text.Encoding.UTF8.GetBytes(value.ToLowerInvariant())); bytes[6] = (byte)((bytes[6] & 0x0f) | 0x30); bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80); return new Guid(bytes).ToString();
    }

    private static void WriteAtomically(string path, string content) { var temporary = $"{path}.{Guid.NewGuid():N}.tmp"; File.WriteAllText(temporary, content, new UTF8Encoding(false)); File.Move(temporary, path, true); }
    private sealed record CategoryDocument(string Version, IReadOnlyList<CategoryNode> Nodes);
    private sealed record TreeIndex(string Version, string ProjectName, int TotalAssets, IReadOnlyList<string> RootCategories, IReadOnlyDictionary<string, CategoryPages> CategoryPages);
    private sealed record CategoryPages(string Dir, IReadOnlyList<string> Pages, int RecordCount);
    private sealed record CategoryData(string CategoryId, int Page, IReadOnlyList<AssetRecord> Records);
    private sealed class CategoryNode
    {
        public CategoryNode(string id, string name, string? parentId, int order, int count, int totalCount) { Id = id; Name = name; ParentId = parentId; Order = order; Count = count; TotalCount = totalCount; }
        public string Id { get; set; }
        public string Name { get; set; }
        public string? ParentId { get; set; }
        public int Order { get; set; }
        public int Count { get; set; }
        public int TotalCount { get; set; }
    }
}
