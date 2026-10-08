namespace FrameTrace.Editor.Services;

public static class VideoQualityMatcher
{
    public static string Match(VideoDimensions dimensions, IReadOnlyList<QualityOption> options)
    {
        if (dimensions.Width <= 0 || dimensions.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions), "视频尺寸必须为正数。");
        }

        var shortEdge = Math.Min(dimensions.Width, dimensions.Height);
        var aspectRatio = (double)Math.Max(dimensions.Width, dimensions.Height) / shortEdge;
        var orderedOptions = options
            .Where(option => option.MinShortEdge >= 0 && option.MaxAspectRatio > 0)
            .OrderByDescending(option => option.MinShortEdge);

        var match = orderedOptions.FirstOrDefault(option =>
            shortEdge >= option.MinShortEdge &&
            aspectRatio <= option.MaxAspectRatio);

        return match?.Name ?? "未分类";
    }
}
