using System.IO;
using System.Text.RegularExpressions;
using FrameTrace.Editor.Domain;

namespace FrameTrace.Editor.Services;

public static class PromptReferenceLinker
{
    private static readonly Regex ImageReferencePattern = new(@"\[@(?<label>Image\s+\d+)\s*->\s*(?<path>[^\]\r\n]+)\]", RegexOptions.IgnoreCase);

    public static string ReplaceImagePaths(string prompt, IReadOnlyList<ReferenceResource> references)
    {
        var imagePaths = references
            .Where(reference => string.Equals(reference.Type, "image", StringComparison.OrdinalIgnoreCase))
            .GroupBy(reference => Path.GetFileName(reference.LocalPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().LocalPath, StringComparer.OrdinalIgnoreCase);

        return ImageReferencePattern.Replace(prompt, match =>
        {
            var originalPath = match.Groups["path"].Value.Trim();
            var fileName = Path.GetFileName(originalPath.Replace('/', Path.DirectorySeparatorChar));
            return imagePaths.TryGetValue(fileName, out var publishedPath)
                ? $"[@{match.Groups["label"].Value.Trim()} -> {publishedPath}]"
                : match.Value;
        });
    }

}