using System.Globalization;

namespace FrameTrace.Editor.Services;

public static class AspectRatioMatcher
{
    public static AspectRatioMatch Match(string? value, IReadOnlyList<AspectRatioOption> options)
    {
        if (!TryParse(value, out var ratio))
        {
            return AspectRatioMatch.Invalid;
        }

        var option = options.FirstOrDefault(candidate => ratio >= candidate.Min && ratio <= candidate.Max);
        return option is null
            ? new AspectRatioMatch(value, null, $"宽高比 {value} 不在已配置的常见比例范围内。")
            : new AspectRatioMatch(value, option.Name, null);
    }

    public static bool TryParse(string? value, out double ratio)
    {
        ratio = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().Replace('：', ':');
        var parts = normalized.Split(':', 2);
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var width) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var height) &&
            width > 0 &&
            height > 0)
        {
            ratio = width / height;
            return true;
        }

        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out ratio) &&
            ratio > 0;
    }
}

public sealed record AspectRatioMatch(string? Original, string? Standard, string? Warning)
{
    public static AspectRatioMatch Invalid { get; } =
        new(null, null, "宽高比为空或格式无法识别。");
}
