using System.Text;
using System.Text.Json;
using System.IO;

namespace FrameTrace.Editor.Services;

public sealed class ViewerOrderDocument
{
    public string Version { get; set; } = string.Empty;
    public Dictionary<string, List<string>> CategoryChildren { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> CategoryAssets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class ViewerOrderStore
{
    public const string RootKey = "$root";
    private const string OrderVariable = "window.VIEWER_ORDER =";
    private const string CategoriesVariable = "window.VIEWER_CATEGORIES =";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = new SnakeCaseNamingPolicy(), WriteIndented = true };

    public static ViewerOrderDocument Read(string webViewerDirectory)
    {
        var path = Path.Combine(webViewerDirectory, "data", "order.js");
        if (File.Exists(path))
        {
            var script = File.ReadAllText(path);
            var start = script.IndexOf(OrderVariable, StringComparison.Ordinal);
            if (start >= 0)
            {
                return JsonSerializer.Deserialize<ViewerOrderDocument>(script[(start + OrderVariable.Length)..].Trim().TrimEnd(';'), JsonOptions)
                    ?? new ViewerOrderDocument();
            }
        }

        return ReadLegacy(Path.Combine(webViewerDirectory, "data", "categories.js"));
    }

    public static void Write(string webViewerDirectory, ViewerOrderDocument document)
    {
        document.Version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var path = Path.Combine(webViewerDirectory, "data", "order.js");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"{OrderVariable} {JsonSerializer.Serialize(document, JsonOptions)};{Environment.NewLine}", new UTF8Encoding(false));
    }

    public static List<string> Siblings(ViewerOrderDocument document, string? parentId) =>
        document.CategoryChildren.TryGetValue(parentId ?? RootKey, out var ids) ? ids : [];

    public static List<string> Assets(ViewerOrderDocument document, string categoryId) =>
        document.CategoryAssets.TryGetValue(categoryId, out var ids) ? ids : [];

    public static void SetSiblings(ViewerOrderDocument document, string? parentId, IEnumerable<string> ids) =>
        document.CategoryChildren[parentId ?? RootKey] = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static void SetAssets(ViewerOrderDocument document, string categoryId, IEnumerable<string> ids) =>
        document.CategoryAssets[categoryId] = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static ViewerOrderDocument ReadLegacy(string categoriesPath)
    {
        var result = new ViewerOrderDocument();
        if (!File.Exists(categoriesPath)) return result;
        try
        {
            var script = File.ReadAllText(categoriesPath);
            var start = script.IndexOf(CategoriesVariable, StringComparison.Ordinal);
            if (start < 0) return result;
            using var json = JsonDocument.Parse(script[(start + CategoriesVariable.Length)..].Trim().TrimEnd(';'));
            if (!json.RootElement.TryGetProperty("nodes", out var nodes)) return result;
            var groups = new Dictionary<string, List<(string Id, int Order)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in nodes.EnumerateArray())
            {
                if (!node.TryGetProperty("id", out var idProperty)) continue;
                var id = idProperty.GetString();
                if (string.IsNullOrWhiteSpace(id)) continue;
                var parent = node.TryGetProperty("parent_id", out var parentProperty) && parentProperty.ValueKind != JsonValueKind.Null
                    ? parentProperty.GetString() ?? RootKey : RootKey;
                var order = node.TryGetProperty("order", out var orderProperty) && orderProperty.TryGetInt32(out var value) ? value : int.MaxValue;
                if (!groups.TryGetValue(parent, out var children)) groups[parent] = children = [];
                children.Add((id, order));
                if (node.TryGetProperty("asset_ids", out var assetIds) && assetIds.ValueKind == JsonValueKind.Array)
                    result.CategoryAssets[id] = assetIds.EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)).Cast<string>().ToList();
            }
            foreach (var (parent, children) in groups)
                result.CategoryChildren[parent] = children.OrderBy(item => item.Order).ThenBy(item => item.Id, StringComparer.Ordinal).Select(item => item.Id).ToList();
        }
        catch (Exception exception) when (exception is IOException or JsonException) { }
        return result;
    }
}
