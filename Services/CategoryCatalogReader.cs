using System.IO;
using System.Text.Json;

namespace FrameTrace.Editor.Services;

public sealed class CategoryCatalogReader
{
    private const string CategoriesVariable = "window.VIEWER_CATEGORIES =";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = new SnakeCaseNamingPolicy() };

    public IReadOnlyList<string> ReadPaths(string webViewerDirectory)
    {
        var path = Path.Combine(webViewerDirectory, "data", "categories.js");
        if (!File.Exists(path)) return [];
        try
        {
            var script = File.ReadAllText(path);
            var start = script.IndexOf(CategoriesVariable, StringComparison.Ordinal);
            if (start < 0) return [];
            var json = script[(start + CategoriesVariable.Length)..].Trim().TrimEnd(';');
            var document = JsonSerializer.Deserialize<CategoryDocument>(json, JsonOptions);
            if (document?.Nodes is null) return [];
            var order = ViewerOrderStore.Read(webViewerDirectory);
            var nodes = document.Nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
            var paths = new List<string>();
            foreach (var node in OrderedChildren(null, nodes, order))
            {
                AddPath(node, string.Empty, nodes, paths, order);
            }
            return paths;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            return [];
        }
    }

    private static void AddPath(CategoryNode node, string parentPath, IReadOnlyDictionary<string, CategoryNode> nodes, List<string> paths, ViewerOrderDocument order)
    {
        var path = parentPath.Length == 0 ? node.Name : $"{parentPath} / {node.Name}";
        paths.Add(path);
        foreach (var child in OrderedChildren(node.Id, nodes, order))
        {
            AddPath(child, path, nodes, paths, order);
        }
    }

    private static IEnumerable<CategoryNode> OrderedChildren(string? parentId, IReadOnlyDictionary<string, CategoryNode> nodes, ViewerOrderDocument order)
    {
        var ids = ViewerOrderStore.Siblings(order, parentId);
        return ids.Where(nodes.ContainsKey).Select(id => nodes[id])
            .Concat(nodes.Values.Where(node => string.Equals(node.ParentId, parentId, StringComparison.OrdinalIgnoreCase) && !ids.Contains(node.Id, StringComparer.OrdinalIgnoreCase))
                .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase));
    }

    private sealed record CategoryDocument(string Version, IReadOnlyList<CategoryNode> Nodes);

    private sealed class CategoryNode
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? ParentId { get; set; }
    }
}
