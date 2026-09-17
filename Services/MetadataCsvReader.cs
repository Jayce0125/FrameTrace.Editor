using System.IO;
using System.Globalization;
using System.Text;

namespace FrameTrace.Editor.Services;

public static class MetadataCsvReader
{
    public static ImportMetadata Read(string recordDirectory)
    {
        var metadataPath = Path.Combine(recordDirectory, "metadata.csv");
        if (!File.Exists(metadataPath))
        {
            return new ImportMetadata(null, null, null, null, "缺少必要的 metadata.csv 文件。");
        }

        try
        {
            var values = ReadLines(metadataPath)
                .Select(line => line.Split(',', 2))
                .Where(columns => columns.Length == 2 && !IsHeader(columns[0]))
                .ToDictionary(columns => columns[0].Trim(), columns => columns[1].Trim(), StringComparer.OrdinalIgnoreCase);
            var createdText = GetValue(values, "创建时间", "created");
            DateTimeOffset? created = DateTimeOffset.TryParse(createdText, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsedCreated)
                ? parsedCreated
                : null;
            var invalidCreated = !string.IsNullOrWhiteSpace(createdText) && created is null;
            return new ImportMetadata(
                created,
                GetValue(values, "清晰度", "quality"),
                GetValue(values, "宽高比", "aspect_ratio"),
                GetValue(values, "生成模型或功能", "生成模型", "功能", "feature"),
                invalidCreated ? "metadata.csv 中的“创建时间”无法识别，请使用如 2026-09-17 14:30 的时间格式。" : null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new ImportMetadata(null, null, null, null, $"无法读取 metadata.csv：{exception.Message}");
        }
    }

    private static IEnumerable<string> ReadLines(string metadataPath)
    {
        try
        {
            return File.ReadAllLines(metadataPath, new UTF8Encoding(false, true));
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return File.ReadAllLines(metadataPath, Encoding.GetEncoding(936));
        }
    }

    private static bool IsHeader(string value) =>
        string.Equals(value.Trim(), "字段", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value.Trim(), "field", StringComparison.OrdinalIgnoreCase);

    private static string? GetValue(IReadOnlyDictionary<string, string> values, params string[] fields)
    {
        foreach (var field in fields)
        {
            if (values.TryGetValue(field, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}

public sealed record ImportMetadata(DateTimeOffset? Created, string? Quality, string? AspectRatio, string? Feature, string? Warning)
{
    public static ImportMetadata Empty { get; } = new(null, null, null, null, null);
}