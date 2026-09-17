using System.IO;
using System.Text.Json;

namespace FrameTrace.Editor.Services;

public sealed class LibraryConfiguration
{
    public string ProjectName { get; init; } = "灵映素材库";

    public string AssetsDirectory { get; init; } = string.Empty;

    public string WebViewerDirectory { get; init; } = string.Empty;

    public string FfmpegPath { get; init; } = "ffmpeg";

    public string FfprobePath { get; init; } = "ffprobe";

    public int ThumbnailWidth { get; init; } = 640;

    public int ThumbnailQuality { get; init; } = 80;

    public double ThumbnailPositionFraction { get; init; } = 0.1;

    public int ViewerPageSize { get; init; } = 100;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AssetsDirectory) &&
        !string.IsNullOrWhiteSpace(WebViewerDirectory);
}

public static class LibraryConfigurationLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static LibraryConfiguration Load()
    {
        var configurationPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        if (!File.Exists(configurationPath))
        {
            return new LibraryConfiguration();
        }

        return JsonSerializer.Deserialize<LibraryConfiguration>(File.ReadAllText(configurationPath), JsonOptions)
            ?? new LibraryConfiguration();
    }
}