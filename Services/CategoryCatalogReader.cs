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
            var nodes = document.Nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
            var paths = new List<(int Order, string Path)>();
            foreach (var node in document.Nodes.Where(node => node.ParentId is null).OrderBy(node => node.Order))
            {
                AddPath(node, string.Empty, nodes, paths);
            }
            return paths.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).Select(item => item.Path).ToArray();
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            return [];
        }
    }

    private static void AddPath(CategoryNode node, string parentPath, IReadOnlyDictionary<string, CategoryNode> nodes, List<(int Order, string Path)> paths)
    {
        var path = parentPath.Length == 0 ? node.Name : $"{parentPath} / {node.Name}";
        paths.Add((node.Order, path));
        foreach (var child in nodes.Values.Where(child => string.Equals(child.ParentId, node.Id, StringComparison.OrdinalIgnoreCase)).OrderBy(child => child.Order))
        {
            AddPath(child, path, nodes, paths);
        }
    }

    private sealed record CategoryDocument(string Version, IReadOnlyList<CategoryNode> Nodes);

    private sealed class CategoryNode
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? ParentId { get; set; }
        public int Order { get; set; }
    }
}
