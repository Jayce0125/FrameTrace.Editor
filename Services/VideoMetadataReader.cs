using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace FrameTrace.Editor.Services;

public static class VideoMetadataReader
{
    public static VideoDimensions? ReadDimensions(string videoPath, LibraryConfiguration configuration)
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
            startInfo.ArgumentList.Add("-select_streams");
            startInfo.ArgumentList.Add("v:0");
            startInfo.ArgumentList.Add("-show_entries");
            startInfo.ArgumentList.Add("stream=width,height");
            startInfo.ArgumentList.Add("-of");
            startInfo.ArgumentList.Add("csv=p=0:s=x");
            startInfo.ArgumentList.Add(videoPath);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return null;
            }

            var dimensions = output.Split('x', 2);
            return dimensions.Length == 2 &&
                int.TryParse(dimensions[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) &&
                int.TryParse(dimensions[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) &&
                width > 0 &&
                height > 0
                ? new VideoDimensions(width, height)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

public sealed record VideoDimensions(int Width, int Height);
