using System.IO;
using System.Text;
using System.Text.Json;
using FrameTrace.Editor.Domain;

namespace FrameTrace.Editor.Services;

public sealed class CategoryManagementService
{
    private const string CategoriesVariable = "window.VIEWER_CATEGORIES =";
    private const string RecordVariable = "window.FRAME_TRACE_RECORD =";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = new SnakeCaseNamingPolicy(), WriteIndented = true };

    public IReadOnlyList<CategoryNodeModel> Load(string webViewerDirectory)
    {
        try
        {
            var document = ReadDocument(Path.Combine(webViewerDirectory, "data", "categories.js"));
            return document?.Nodes ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    public void Create(string webViewerDirectory, string name, string? parentId)
    {
        var path = Path.Combine(webViewerDirectory, "data", "categories.js");
        var document = ReadDocument(path) ?? new CategoryDocument
        {
            Version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
            Nodes = []
        };
        EnsureNameAvailable(document.Nodes, name, parentId, null);
        var order = document.Nodes.Where(node => node.ParentId == parentId).Select(node => node.Order).DefaultIfEmpty(-1).Max() + 1;
        document.Nodes.Add(new CategoryNodeModel(Guid.NewGuid().ToString(), name, parentId, order, 0, 0));
        WriteDocument(path, document);
    }

    public void Rename(string assetsDirectory, string webViewerDirectory, string categoryId, string newName)
    {
        var path = Path.Combine(webViewerDirectory, "data", "categories.js");
        var document = ReadDocument(path) ?? throw new InvalidDataException("未找到分类索引。");
        var node = Find(document.Nodes, categoryId);
        EnsureNameAvailable(document.Nodes, newName, node.ParentId, categoryId);
        var oldPath = BuildPath(document.Nodes, categoryId);
        node.Name = newName;
        var newPath = BuildPath(document.Nodes, categoryId);
        RewriteRecordPaths(assetsDirectory, oldPath, newPath);
        document.Version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        WriteDocument(path, document);
    }

    public void Move(string assetsDirectory, string webViewerDirectory, string categoryId, string? parentId)
    {
        var path = Path.Combine(webViewerDirectory, "data", "categories.js");
        var document = ReadDocument(path) ?? throw new InvalidDataException("未找到分类索引。");
        var node = Find(document.Nodes, categoryId);
        if (string.Equals(categoryId, parentId, StringComparison.OrdinalIgnoreCase) || IsDescendant(document.Nodes, parentId, categoryId)) throw new InvalidOperationException("不能将分类移动到自身或其子分类下。");
        EnsureNameAvailable(document.Nodes, node.Name, parentId, categoryId);
        var oldPath = BuildPath(document.Nodes, categoryId);
        node.ParentId = parentId;
        node.Order = document.Nodes.Where(item => item.ParentId == parentId && item.Id != categoryId).Select(item => item.Order).DefaultIfEmpty(-1).Max() + 1;
        var newPath = BuildPath(document.Nodes, categoryId);
        RewriteRecordPaths(assetsDirectory, oldPath, newPath);
        document.Version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        WriteDocument(path, document);
    }

    public void Reorder(string webViewerDirectory, string categoryId, int direction)
    {
        var path = Path.Combine(webViewerDirectory, "data", "categories.js");
        var document = ReadDocument(path) ?? throw new InvalidDataException("未找到分类索引。");
        var node = Find(document.Nodes, categoryId);
        var siblings = document.Nodes.Where(item => item.ParentId == node.ParentId).OrderBy(item => item.Order).ToList();
        var index = siblings.FindIndex(item => item.Id == categoryId); var target = index + direction;
        if (target < 0 || target >= siblings.Count) return;
        (siblings[index].Order, siblings[target].Order) = (siblings[target].Order, siblings[index].Order);
        document.Version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        WriteDocument(path, document);
    }

    public void ReorderBefore(string webViewerDirectory, string categoryId, string targetCategoryId)
    {
        var path = Path.Combine(webViewerDirectory, "data", "categories.js");
        var document = ReadDocument(path) ?? throw new InvalidDataException("未找到分类索引。");
        var source = Find(document.Nodes, categoryId);
        var target = Find(document.Nodes, targetCategoryId);
        if (!string.Equals(source.ParentId, target.ParentId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("只能调整同级分类的顺序。");
        }

        var siblings = document.Nodes.Where(node => node.ParentId == source.ParentId)
            .OrderBy(node => node.Order)
            .ToList();
        siblings.Remove(source);
        siblings.Insert(siblings.FindIndex(node => node.Id == target.Id), source);
        for (var index = 0; index < siblings.Count; index++)
        {
            siblings[index].Order = index;
        }

        document.Version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        WriteDocument(path, document);
    }

    public void ReorderAfter(string webViewerDirectory, string categoryId, string targetCategoryId)
    {
        var path = Path.Combine(webViewerDirectory, "data", "categories.js");
        var document = ReadDocument(path) ?? throw new InvalidDataException("未找到分类索引。");
        var source = Find(document.Nodes, categoryId);
        var target = Find(document.Nodes, targetCategoryId);
        if (!string.Equals(source.ParentId, target.ParentId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("只能调整同级分类的顺序。");
        }

        var siblings = document.Nodes.Where(node => node.ParentId == source.ParentId)
            .OrderBy(node => node.Order)
            .ToList();
        siblings.Remove(source);
        var targetIndex = siblings.FindIndex(node => node.Id == target.Id);
        siblings.Insert(targetIndex + 1, source);
        for (var index = 0; index < siblings.Count; index++)
        {
            siblings[index].Order = index;
        }

        document.Version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        WriteDocument(path, document);
    }

    public void MoveRecords(IReadOnlyList<LibraryAsset> allAssets, IReadOnlyList<LibraryAsset> selectedAssets, string webViewerDirectory, string categoryId)
    {
        var document = ReadDocument(Path.Combine(webViewerDirectory, "data", "categories.js")) ?? throw new InvalidDataException("未找到分类索引。");
        var targetPath = BuildPath(document.Nodes, categoryId);
        var selectedIds = selectedAssets.Select(asset => asset.Record.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nextOrder = allAssets.Where(asset => asset.Record.CategoryId == categoryId).Select(asset => asset.Record.Order).DefaultIfEmpty(-1).Max() + 1;
        foreach (var asset in allAssets.Where(asset => selectedIds.Contains(asset.Record.Id)))
        {
            asset.Record.FolderName = targetPath;
            asset.Record.CategoryId = categoryId;
            asset.Record.Order = nextOrder++;
            var json = JsonSerializer.Serialize(asset.Record, JsonOptions);
            var temporary = $"{asset.RecordPath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporary, $"{RecordVariable} {json};{Environment.NewLine}", new UTF8Encoding(false));
            File.Move(temporary, asset.RecordPath, true);
        }
    }

    public void ReorderRecords(IReadOnlyList<LibraryAsset> allAssets, IReadOnlyList<LibraryAsset> selectedAssets, int direction)
    {
        var selectedIds = selectedAssets.Select(asset => asset.Record.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in allAssets.GroupBy(asset => asset.Record.FolderName, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group.OrderBy(asset => asset.Record.Order).ThenBy(asset => asset.Record.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
            var selected = ordered.Where(asset => selectedIds.Contains(asset.Record.Id)).ToHashSet();
            if (selected.Count == 0) continue;
            var indexes = ordered.Select((asset, index) => (asset, index)).Where(item => selected.Contains(item.asset)).Select(item => item.index).ToArray();
            if (direction < 0 && indexes[0] > 0)
            {
                var previous = ordered[indexes[0] - 1]; ordered.RemoveAt(indexes[0] - 1); ordered.Insert(indexes[^1], previous);
            }
            else if (direction > 0 && indexes[^1] < ordered.Count - 1)
            {
                var next = ordered[indexes[^1] + 1]; ordered.RemoveAt(indexes[^1] + 1); ordered.Insert(indexes[0], next);
            }
            for (var index = 0; index < ordered.Count; index++)
            {
                ordered[index].Record.Order = index;
                PersistRecord(ordered[index].Record);
            }
        }
    }

    public void ReorderAssetAfter(IReadOnlyList<LibraryAsset> allAssets, LibraryAsset source, LibraryAsset target)
    {
        if (!string.Equals(source.Record.FolderName, target.Record.FolderName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("只能调整同一分类内素材的顺序。");
        }

        var ordered = allAssets
            .Where(asset => string.Equals(asset.Record.FolderName, source.Record.FolderName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(asset => asset.Record.Order)
            .ToList();
        ordered.Remove(source);
        var targetIndex = ordered.FindIndex(asset => ReferenceEquals(asset, target));
        if (targetIndex < 0)
        {
            throw new InvalidOperationException("未找到目标素材。");
        }

        ordered.Insert(targetIndex + 1, source);
        for (var index = 0; index < ordered.Count; index++)
        {
            ordered[index].Record.Order = index;
            PersistRecord(ordered[index].Record);
        }
    }

    private static void RewriteRecordPaths(string assetsDirectory, string oldPath, string newPath)
    {
        foreach (var recordPath in Directory.EnumerateFiles(assetsDirectory, "record.js", SearchOption.AllDirectories))
        {
            var script = File.ReadAllText(recordPath); var start = script.IndexOf(RecordVariable, StringComparison.Ordinal); if (start < 0) continue;
            var record = JsonSerializer.Deserialize<AssetRecord>(script[(start + RecordVariable.Length)..].Trim().TrimEnd(';'), JsonOptions); if (record is null) continue;
            if (!string.Equals(record.FolderName, oldPath, StringComparison.OrdinalIgnoreCase) && !record.FolderName.StartsWith(oldPath + "/", StringComparison.OrdinalIgnoreCase)) continue;
            record.FolderName = newPath + record.FolderName[oldPath.Length..];
            var json = JsonSerializer.Serialize(record, JsonOptions); var temporary = $"{recordPath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporary, $"{RecordVariable} {json};{Environment.NewLine}", new UTF8Encoding(false)); File.Move(temporary, recordPath, true);
        }
    }

    private static CategoryDocument? ReadDocument(string path)
    {
        if (!File.Exists(path)) return null;
        var script = File.ReadAllText(path); var start = script.IndexOf(CategoriesVariable, StringComparison.Ordinal); if (start < 0) return null;
        return JsonSerializer.Deserialize<CategoryDocument>(script[(start + CategoriesVariable.Length)..].Trim().TrimEnd(';'), JsonOptions);
    }

    private static void WriteDocument(string path, CategoryDocument document) => File.WriteAllText(path, $"{CategoriesVariable} {JsonSerializer.Serialize(document, JsonOptions)};{Environment.NewLine}", new UTF8Encoding(false));
    private static void PersistRecord(AssetRecord record)
    {
        var json = JsonSerializer.Serialize(record, JsonOptions);
        var path = record.SourcePath ?? throw new InvalidDataException("记录缺少源文件路径。");
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, $"{RecordVariable} {json};{Environment.NewLine}", new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }
    private static CategoryNodeModel Find(IReadOnlyList<CategoryNodeModel> nodes, string id) => nodes.FirstOrDefault(node => string.Equals(node.Id, id, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("未找到目标分类。");
    private static string BuildPath(IReadOnlyList<CategoryNodeModel> nodes, string id) { var result = new List<string>(); var current = Find(nodes, id); while (true) { result.Insert(0, current.Name); if (current.ParentId is null) break; current = Find(nodes, current.ParentId); } return string.Join('/', result); }
    private static bool IsDescendant(IReadOnlyList<CategoryNodeModel> nodes, string? candidate, string ancestor) { while (candidate is not null) { if (candidate == ancestor) return true; candidate = nodes.FirstOrDefault(node => node.Id == candidate)?.ParentId; } return false; }
    private static void EnsureNameAvailable(IReadOnlyList<CategoryNodeModel> nodes, string name, string? parentId, string? exceptId) { if (string.IsNullOrWhiteSpace(name) || nodes.Any(node => node.ParentId == parentId && node.Id != exceptId && string.Equals(node.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("同层已存在同名分类，操作被拒绝。"); }

    private sealed class CategoryDocument
    {
        public string Version { get; set; } = string.Empty;
        public List<CategoryNodeModel> Nodes { get; set; } = [];
    }
}

public sealed class CategoryNodeModel
{
    public CategoryNodeModel() { }
    public CategoryNodeModel(string id, string name, string? parentId, int order, int count, int totalCount) { Id = id; Name = name; ParentId = parentId; Order = order; Count = count; TotalCount = totalCount; }
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ParentId { get; set; }
    public int Order { get; set; }
    public int Count { get; set; }
    public int TotalCount { get; set; }
}
