using System.IO;
using System.Text.Json;

namespace FrameTrace.Editor.Services;

public sealed class LibraryConfiguration
{
    public string ProjectName { get; init; } = "玄机灵界素材库";

    public string AssetsDirectory { get; init; } = string.Empty;

    public string WebViewerDirectory { get; init; } = string.Empty;

    public string FfmpegPath { get; init; } = "ffmpeg";

    public string FfprobePath { get; init; } = "ffprobe";

    public int ThumbnailWidth { get; init; } = 640;

    public int ThumbnailQuality { get; init; } = 80;

    public double ThumbnailPositionFraction { get; init; } = 0.1;

    public int ViewerPageSize { get; init; } = 100;

    public IReadOnlyList<AspectRatioOption> AspectRatioOptions { get; init; } =
    [
        new("1:1", 0.95, 1.05),
        new("4:3", 1.18, 1.39),
        new("3:2", 1.40, 1.60),
        new("16:9", 1.68, 1.88),
        new("21:9", 2.20, 2.45),
        new("9:16", 0.53, 0.60),
        new("3:4", 0.72, 0.80),
        new("2:3", 0.62, 0.70)
    ];

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AssetsDirectory) &&
        !string.IsNullOrWhiteSpace(WebViewerDirectory);
}

public sealed record AspectRatioOption(string Name, double Min, double Max);

public sealed class PathConfiguration
{
    public string AssetsDirectory { get; init; } = string.Empty;

    public string WebViewerDirectory { get; init; } = string.Empty;

    public string FfmpegPath { get; init; } = "ffmpeg";

    public string FfprobePath { get; init; } = "ffprobe";
}

public sealed class SettingsConfiguration
{
    public string ProjectName { get; init; } = "玄机灵界素材库";

    public int ThumbnailWidth { get; init; } = 640;

    public int ThumbnailQuality { get; init; } = 80;

    public double ThumbnailPositionFraction { get; init; } = 0.1;

    public int ViewerPageSize { get; init; } = 100;

    public IReadOnlyList<AspectRatioOption> AspectRatioOptions { get; init; } =
    [
        new("1:1", 0.95, 1.05),
        new("4:3", 1.18, 1.39),
        new("3:2", 1.40, 1.60),
        new("16:9", 1.68, 1.88),
        new("21:9", 2.20, 2.45),
        new("9:16", 0.53, 0.60),
        new("3:4", 0.72, 0.80),
        new("2:3", 0.62, 0.70)
    ];
}

public static class LibraryConfigurationLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = new SnakeCaseNamingPolicy(),
        PropertyNameCaseInsensitive = true
    };

    public static LibraryConfiguration Load()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var pathConfigurationPath = Path.Combine(baseDirectory, "config.json");
        var settingsConfigurationPath = Path.Combine(baseDirectory, "settings.json");
        if (!File.Exists(pathConfigurationPath) && !File.Exists(settingsConfigurationPath))
        {
            return new LibraryConfiguration();
        }

        if (!File.Exists(settingsConfigurationPath))
        {
            return JsonSerializer.Deserialize<LibraryConfiguration>(
                File.ReadAllText(pathConfigurationPath), JsonOptions) ?? new LibraryConfiguration();
        }

        var paths = File.Exists(pathConfigurationPath)
            ? JsonSerializer.Deserialize<PathConfiguration>(File.ReadAllText(pathConfigurationPath), JsonOptions)
            : new PathConfiguration();
        var settings = JsonSerializer.Deserialize<SettingsConfiguration>(
            File.ReadAllText(settingsConfigurationPath), JsonOptions) ?? new SettingsConfiguration();

        return new LibraryConfiguration
        {
            ProjectName = settings.ProjectName,
            AssetsDirectory = paths?.AssetsDirectory ?? string.Empty,
            WebViewerDirectory = paths?.WebViewerDirectory ?? string.Empty,
            FfmpegPath = paths?.FfmpegPath ?? "ffmpeg",
            FfprobePath = paths?.FfprobePath ?? "ffprobe",
            ThumbnailWidth = settings.ThumbnailWidth,
            ThumbnailQuality = settings.ThumbnailQuality,
            ThumbnailPositionFraction = settings.ThumbnailPositionFraction,
            ViewerPageSize = settings.ViewerPageSize,
            AspectRatioOptions = settings.AspectRatioOptions
        };
    }
}