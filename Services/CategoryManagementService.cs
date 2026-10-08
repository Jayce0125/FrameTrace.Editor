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
        try { return ReadDocument(Path.Combine(webViewerDirectory, "data", "categories.js"))?.Nodes ?? []; }
        catch (Exception) { return []; }
    }

    public ViewerOrderDocument LoadOrder(string webViewerDirectory) => ViewerOrderStore.Read(webViewerDirectory);

    // record.js 的 category_id 可能缺失或过期（历史数据为空串；Rename 只改 folder_name 不同步它），
    // 拖拽排序用它作 order.js 的键，键错了写入就会被网页端丢弃。加载时按 folder_name 解析真实 ID 并回写。
    public void SynchronizeRecordCategoryIds(IReadOnlyList<LibraryAsset> allAssets, string webViewerDirectory)
    {
        var document = ReadDocument(Path.Combine(webViewerDirectory, "data", "categories.js"));
        var byPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (document is not null)
            foreach (var node in document.Nodes)
                if (!string.IsNullOrWhiteSpace(node.Id) && !string.IsNullOrWhiteSpace(node.Name))
                    byPath[BuildPath(document.Nodes, node.Id)] = node.Id;
        foreach (var asset in allAssets)
        {
            var folder = NormalizeFolderPath(asset.Record.FolderName);
            if (folder is null) continue;
            var resolved = byPath.TryGetValue(folder, out var id) ? id : TreeViewerIndexPublisher.CreateStableId(folder);
            if (string.Equals(asset.Record.CategoryId, resolved, StringComparison.OrdinalIgnoreCase)) continue;
            asset.Record.CategoryId = resolved;
            try { PersistRecord(asset.Record); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { }
        }
    }

    private static string? NormalizeFolderPath(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return null;
        var normalized = folderName.Replace('\\', '/').Trim().TrimEnd('/');
        return normalized.Length == 0 ? null : normalized;
    }

    public void Create(string webViewerDirectory, string name, string? parentId)
    {
        var path = Path.Combine(webViewerDirectory, "data", "categories.js");
        var document = ReadDocument(path) ?? new CategoryDocument { Version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(), Nodes = [] };
        EnsureNameAvailable(document.Nodes, name, parentId, null);
        var node = new CategoryNodeModel(Guid.NewGuid().ToString(), name.Trim(), parentId, 0, 0);
        document.Nodes.Add(node);
        var order = ViewerOrderStore.Read(webViewerDirectory);
        ViewerOrderStore.SetSiblings(order, parentId, ViewerOrderStore.Siblings(order, parentId).Append(node.Id));
        WriteDocument(path, document);
        ViewerOrderStore.Write(webViewerDirectory, order);
    }

    public void Rename(string assetsDirectory, string webViewerDirectory, string categoryId, string newName)
    {
        var path = Path.Combine(webViewerDirectory, "data", "categories.js");
        var document = ReadDocument(path) ?? throw new InvalidDataException("未找到分类索引。");
        var node = Find(document.Nodes, categoryId);
        EnsureNameAvailable(document.Nodes, newName, node.ParentId, categoryId);
        var oldPath = BuildPath(document.Nodes, categoryId);
        node.Name = newName.Trim();
        RewriteRecordPaths(assetsDirectory, oldPath, BuildPath(document.Nodes, categoryId));
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
        var order = ViewerOrderStore.Read(webViewerDirectory);
        ViewerOrderStore.SetSiblings(order, node.ParentId, ViewerOrderStore.Siblings(order, node.ParentId).Where(id => !string.Equals(id, categoryId, StringComparison.OrdinalIgnoreCase)));
        node.ParentId = parentId;
        ViewerOrderStore.SetSiblings(order, parentId, ViewerOrderStore.Siblings(order, parentId).Where(id => !string.Equals(id, categoryId, StringComparison.OrdinalIgnoreCase)).Append(categoryId));
        RewriteRecordPaths(assetsDirectory, oldPath, BuildPath(document.Nodes, categoryId));
        WriteDocument(path, document);
        ViewerOrderStore.Write(webViewerDirectory, order);
    }

    public void Reorder(string webViewerDirectory, string categoryId, int direction)
    {
        var document = RequireDocument(webViewerDirectory); var node = Find(document.Nodes, categoryId); var order = ViewerOrderStore.Read(webViewerDirectory);
        var siblings = Complete(ViewerOrderStore.Siblings(order, node.ParentId), document.Nodes.Where(item => item.ParentId == node.ParentId).Select(item => item.Id));
        var index = siblings.FindIndex(id => string.Equals(id, categoryId, StringComparison.OrdinalIgnoreCase)); var target = index + direction;
        if (index >= 0 && target >= 0 && target < siblings.Count) (siblings[index], siblings[target]) = (siblings[target], siblings[index]);
        ViewerOrderStore.SetSiblings(order, node.ParentId, siblings); ViewerOrderStore.Write(webViewerDirectory, order);
    }

    public void ReorderBefore(string webViewerDirectory, string categoryId, string targetCategoryId) => ReorderRelative(webViewerDirectory, categoryId, targetCategoryId, false);
    public void ReorderAfter(string webViewerDirectory, string categoryId, string targetCategoryId) => ReorderRelative(webViewerDirectory, categoryId, targetCategoryId, true);

    public void MoveRecords(IReadOnlyList<LibraryAsset> allAssets, IReadOnlyList<LibraryAsset> selectedAssets, string webViewerDirectory, string categoryId)
    {
        var document = RequireDocument(webViewerDirectory); var targetPath = BuildPath(document.Nodes, categoryId); Find(document.Nodes, categoryId);
        var selectedIds = selectedAssets.Select(asset => asset.Record.Id).ToHashSet(StringComparer.OrdinalIgnoreCase); var order = ViewerOrderStore.Read(webViewerDirectory);
        foreach (var asset in allAssets.Where(asset => selectedIds.Contains(asset.Record.Id))) { asset.Record.FolderName = targetPath; asset.Record.CategoryId = categoryId; PersistRecord(asset.Record); }
        foreach (var key in order.CategoryAssets.Keys.ToArray()) ViewerOrderStore.SetAssets(order, key, ViewerOrderStore.Assets(order, key).Where(id => !selectedIds.Contains(id)));
        ViewerOrderStore.SetAssets(order, categoryId, ViewerOrderStore.Assets(order, categoryId).Where(id => !selectedIds.Contains(id)).Concat(selectedAssets.Select(asset => asset.Record.Id)));
        ViewerOrderStore.Write(webViewerDirectory, order);
    }

    public void ReorderRecords(IReadOnlyList<LibraryAsset> allAssets, IReadOnlyList<LibraryAsset> selectedAssets, int direction, string webViewerDirectory)
    {
        var selectedIds = selectedAssets.Select(asset => asset.Record.Id).ToHashSet(StringComparer.OrdinalIgnoreCase); var order = ViewerOrderStore.Read(webViewerDirectory);
        foreach (var group in allAssets.GroupBy(asset => asset.Record.CategoryId, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(group.Key)) continue;
            var ordered = OrderAssets(order, group.Key, group).ToList(); var indexes = ordered.Select((asset, index) => (asset, index)).Where(item => selectedIds.Contains(item.asset.Record.Id)).Select(item => item.index).ToArray();
            if (indexes.Length == 0) continue;
            if (direction < 0 && indexes[0] > 0) { var previous = ordered[indexes[0] - 1]; ordered.RemoveAt(indexes[0] - 1); ordered.Insert(indexes[^1], previous); }
            else if (direction > 0 && indexes[^1] < ordered.Count - 1) { var next = ordered[indexes[^1] + 1]; ordered.RemoveAt(indexes[^1] + 1); ordered.Insert(indexes[0], next); }
            ViewerOrderStore.SetAssets(order, group.Key, ordered.Select(asset => asset.Record.Id));
        }
        ViewerOrderStore.Write(webViewerDirectory, order);
    }

    public void SortRecordsByName(IReadOnlyList<LibraryAsset> allAssets, string categoryId, string webViewerDirectory)
    {
        var order = ViewerOrderStore.Read(webViewerDirectory);
        var records = allAssets.Where(asset => string.Equals(asset.Record.CategoryId, categoryId, StringComparison.OrdinalIgnoreCase));
        var sorted = OrderAssets(order, categoryId, records).OrderBy(asset => asset, AssetNameComparer.Instance);
        ViewerOrderStore.SetAssets(order, categoryId, sorted.Select(asset => asset.Record.Id));
        ViewerOrderStore.Write(webViewerDirectory, order);
    }

    public void ReorderAssetAfter(IReadOnlyList<LibraryAsset> allAssets, LibraryAsset source, LibraryAsset target, string webViewerDirectory)
    {
        if (!string.Equals(source.Record.CategoryId, target.Record.CategoryId, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("只能调整同一分类内素材的顺序。");
        var order = ViewerOrderStore.Read(webViewerDirectory); var ordered = OrderAssets(order, source.Record.CategoryId, allAssets.Where(asset => string.Equals(asset.Record.CategoryId, source.Record.CategoryId, StringComparison.OrdinalIgnoreCase))).ToList();
        ordered.RemoveAll(asset => string.Equals(asset.Record.Id, source.Record.Id, StringComparison.OrdinalIgnoreCase)); var targetIndex = ordered.FindIndex(asset => string.Equals(asset.Record.Id, target.Record.Id, StringComparison.OrdinalIgnoreCase));
        if (targetIndex < 0) throw new InvalidOperationException("未找到目标素材。");
        ordered.Insert(targetIndex + 1, source); ViewerOrderStore.SetAssets(order, source.Record.CategoryId, ordered.Select(asset => asset.Record.Id)); ViewerOrderStore.Write(webViewerDirectory, order);
    }

    private void ReorderRelative(string webViewerDirectory, string categoryId, string targetCategoryId, bool after)
    {
        var document = RequireDocument(webViewerDirectory); var source = Find(document.Nodes, categoryId); var target = Find(document.Nodes, targetCategoryId);
        if (!string.Equals(source.ParentId, target.ParentId, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("只能调整同级分类的顺序。");
        var order = ViewerOrderStore.Read(webViewerDirectory); var siblings = Complete(ViewerOrderStore.Siblings(order, source.ParentId), document.Nodes.Where(node => node.ParentId == source.ParentId).Select(node => node.Id));
        siblings.RemoveAll(id => string.Equals(id, categoryId, StringComparison.OrdinalIgnoreCase)); var targetIndex = siblings.FindIndex(id => string.Equals(id, targetCategoryId, StringComparison.OrdinalIgnoreCase)); siblings.Insert(targetIndex + (after ? 1 : 0), categoryId);
        ViewerOrderStore.SetSiblings(order, source.ParentId, siblings); ViewerOrderStore.Write(webViewerDirectory, order);
    }

    private static IEnumerable<LibraryAsset> OrderAssets(ViewerOrderDocument order, string categoryId, IEnumerable<LibraryAsset> assets)
    {
        var byId = assets.ToDictionary(asset => asset.Record.Id, StringComparer.OrdinalIgnoreCase); var listed = ViewerOrderStore.Assets(order, categoryId).Where(byId.ContainsKey);
        return listed.Select(id => byId[id]).Concat(byId.Values.Where(asset => !listed.Contains(asset.Record.Id, StringComparer.OrdinalIgnoreCase)).OrderBy(asset => asset.Record.DisplayName, StringComparer.OrdinalIgnoreCase));
    }

    private sealed class AssetNameComparer : IComparer<LibraryAsset>
    {
        public static readonly AssetNameComparer Instance = new();

        public int Compare(LibraryAsset? left, LibraryAsset? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;

            var leftName = left.Record.DisplayName;
            var rightName = right.Record.DisplayName;
            var leftIsNumeric = IsNumericName(leftName);
            var rightIsNumeric = IsNumericName(rightName);
            if (leftIsNumeric && rightIsNumeric) return CompareNumericNames(leftName, rightName);
            if (leftIsNumeric != rightIsNumeric) return leftIsNumeric ? -1 : 1;
            return string.Compare(leftName, rightName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNumericName(string name) =>
            name.Length > 0 && name.All(character => character is >= '0' and <= '9');

        private static int CompareNumericNames(string left, string right)
        {
            var leftFirstDigit = left.TakeWhile(character => character == '0').Count();
            var rightFirstDigit = right.TakeWhile(character => character == '0').Count();
            var leftDigits = left[leftFirstDigit..];
            var rightDigits = right[rightFirstDigit..];
            if (leftDigits.Length == 0) leftDigits = "0";
            if (rightDigits.Length == 0) rightDigits = "0";

            var lengthComparison = leftDigits.Length.CompareTo(rightDigits.Length);
            if (lengthComparison != 0) return lengthComparison;
            var digitComparison = string.Compare(leftDigits, rightDigits, StringComparison.Ordinal);
            if (digitComparison != 0) return digitComparison;
            // 数值相同的数字名（如 "1" 与 "01"）：前导零少的排前面，保证结果确定（与资源管理器自然排序一致）。
            var leadingZeroComparison = leftFirstDigit.CompareTo(rightFirstDigit);
            return leadingZeroComparison != 0 ? leadingZeroComparison : string.Compare(left, right, StringComparison.Ordinal);
        }
    }

    private static List<string> Complete(IEnumerable<string> preferred, IEnumerable<string> available) { var ids = preferred.ToList(); ids.AddRange(available.Where(id => !ids.Contains(id, StringComparer.OrdinalIgnoreCase))); return ids; }
    private static void RewriteRecordPaths(string assetsDirectory, string oldPath, string newPath) { foreach (var recordPath in Directory.EnumerateFiles(assetsDirectory, "record.js", SearchOption.AllDirectories)) { var script = File.ReadAllText(recordPath); var start = script.IndexOf(RecordVariable, StringComparison.Ordinal); if (start < 0) continue; var record = JsonSerializer.Deserialize<AssetRecord>(script[(start + RecordVariable.Length)..].Trim().TrimEnd(';'), JsonOptions); if (record is null || (!string.Equals(record.FolderName, oldPath, StringComparison.OrdinalIgnoreCase) && !record.FolderName.StartsWith(oldPath + "/", StringComparison.OrdinalIgnoreCase))) continue; record.FolderName = newPath + record.FolderName[oldPath.Length..]; record.SourcePath = recordPath; PersistRecord(record); } }
    private static CategoryDocument RequireDocument(string directory) => ReadDocument(Path.Combine(directory, "data", "categories.js")) ?? throw new InvalidDataException("未找到分类索引。");
    private static CategoryDocument? ReadDocument(string path) { if (!File.Exists(path)) return null; var script = File.ReadAllText(path); var start = script.IndexOf(CategoriesVariable, StringComparison.Ordinal); return start < 0 ? null : JsonSerializer.Deserialize<CategoryDocument>(script[(start + CategoriesVariable.Length)..].Trim().TrimEnd(';'), JsonOptions); }
    private static void WriteDocument(string path, CategoryDocument document) { document.Version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(); File.WriteAllText(path, $"{CategoriesVariable} {JsonSerializer.Serialize(document, JsonOptions)};{Environment.NewLine}", new UTF8Encoding(false)); }
    private static void PersistRecord(AssetRecord record) { var path = record.SourcePath ?? throw new InvalidDataException("记录缺少源文件路径。"); var temporary = $"{path}.{Guid.NewGuid():N}.tmp"; File.WriteAllText(temporary, $"{RecordVariable} {JsonSerializer.Serialize(record, JsonOptions)};{Environment.NewLine}", new UTF8Encoding(false)); File.Move(temporary, path, true); }
    private static CategoryNodeModel Find(IReadOnlyList<CategoryNodeModel> nodes, string id) => nodes.FirstOrDefault(node => string.Equals(node.Id, id, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("未找到目标分类。");
    private static string BuildPath(IReadOnlyList<CategoryNodeModel> nodes, string id) { var result = new List<string>(); var current = Find(nodes, id); while (true) { result.Insert(0, current.Name); if (current.ParentId is null) break; current = Find(nodes, current.ParentId); } return string.Join('/', result); }
    private static bool IsDescendant(IReadOnlyList<CategoryNodeModel> nodes, string? candidate, string ancestor) { while (candidate is not null) { if (candidate == ancestor) return true; candidate = nodes.FirstOrDefault(node => node.Id == candidate)?.ParentId; } return false; }
    private static void EnsureNameAvailable(IReadOnlyList<CategoryNodeModel> nodes, string name, string? parentId, string? exceptId) { if (string.IsNullOrWhiteSpace(name) || nodes.Any(node => node.ParentId == parentId && node.Id != exceptId && string.Equals(node.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("同层已存在同名分类，操作被拒绝。"); }
    private sealed class CategoryDocument { public string Version { get; set; } = string.Empty; public List<CategoryNodeModel> Nodes { get; set; } = []; }
}

public sealed class CategoryNodeModel
{
    public CategoryNodeModel() { }
    public CategoryNodeModel(string id, string name, string? parentId, int count, int totalCount) { Id = id; Name = name; ParentId = parentId; Count = count; TotalCount = totalCount; }
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ParentId { get; set; }
    public int Count { get; set; }
    public int TotalCount { get; set; }
}
