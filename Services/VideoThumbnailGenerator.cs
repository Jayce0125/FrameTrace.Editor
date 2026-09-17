using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace FrameTrace.Editor.Services;

public sealed class VideoThumbnailGenerator
{
    public ThumbnailResult Generate(string videoPath, string destinationDirectory, string assetId, LibraryConfiguration configuration)
    {
        var fileName = $"{assetId}_thumb.webp";
        var destinationPath = Path.Combine(destinationDirectory, fileName);
        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp.webp";
        var attempts = GetSeekPositions(videoPath, configuration).Append<double?>(null);
        var errors = new List<string>();

        foreach (var seekPosition in attempts)
        {
            var result = RunFfmpeg(videoPath, temporaryPath, seekPosition, configuration);
            if (result.IsSuccessful && File.Exists(temporaryPath))
            {
                File.Move(temporaryPath, destinationPath, true);
                return ThumbnailResult.Succeeded(fileName);
            }

            errors.Add(result.ErrorMessage);
            TryDelete(temporaryPath);
        }

        return ThumbnailResult.Failed(string.Join(" ", errors.Where(error => !string.IsNullOrWhiteSpace(error))));
    }

    private static IEnumerable<double?> GetSeekPositions(string videoPath, LibraryConfiguration configuration)
    {
        var duration = TryReadDuration(videoPath, configuration);
        if (duration is > 0)
        {
            yield return duration.Value * configuration.ThumbnailPositionFraction;
        }

        yield return 1;
    }

    private static double? TryReadDuration(string videoPath, LibraryConfiguration configuration)
    {
        try
        {
            var startInfo = new ProcessStartInfo(configuration.FfprobePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-show_entries");
            startInfo.ArgumentList.Add("format=duration");
            startInfo.ArgumentList.Add("-of");
            startInfo.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
            startInfo.ArgumentList.Add(videoPath);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 && double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
                ? duration
                : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static FfmpegRunResult RunFfmpeg(
        string videoPath,
        string temporaryPath,
        double? seekPosition,
        LibraryConfiguration configuration)
    {
        try
        {
            var startInfo = new ProcessStartInfo(configuration.FfmpegPath)
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-y");
            if (seekPosition.HasValue)
            {
                startInfo.ArgumentList.Add("-ss");
                startInfo.ArgumentList.Add(seekPosition.Value.ToString("0.###", CultureInfo.InvariantCulture));
            }

            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(videoPath);
            startInfo.ArgumentList.Add("-frames:v");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-vf");
            startInfo.ArgumentList.Add($"scale={configuration.ThumbnailWidth}:-2");
            startInfo.ArgumentList.Add("-c:v");
            startInfo.ArgumentList.Add("libwebp");
            startInfo.ArgumentList.Add("-q:v");
            startInfo.ArgumentList.Add(configuration.ThumbnailQuality.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(temporaryPath);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return FfmpegRunResult.Failed("无法启动 FFmpeg。");
            }

            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0
                ? FfmpegRunResult.Succeeded()
                : FfmpegRunResult.Failed($"FFmpeg 退出码 {process.ExitCode}: {error.Trim()}");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            return FfmpegRunResult.Failed($"无法启动 FFmpeg: {exception.Message}");
        }
    }

    private static void TryDelete(string filePath)
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }
}

public sealed record ThumbnailResult(bool IsSuccessful, string? FileName, string? ErrorMessage)
{
    public static ThumbnailResult Succeeded(string fileName) => new(true, fileName, null);

    public static ThumbnailResult Failed(string errorMessage) => new(false, null, errorMessage);
}

public sealed record FfmpegRunResult(bool IsSuccessful, string ErrorMessage)
{
    public static FfmpegRunResult Succeeded() => new(true, string.Empty);

    public static FfmpegRunResult Failed(string errorMessage) => new(false, errorMessage);
}