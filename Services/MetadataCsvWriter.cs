using System.Globalization;
using System.IO;
using System.Text;

namespace FrameTrace.Editor.Services;

public static class MetadataCsvWriter
{
    public static void WriteCreatedTime(string metadataPath, DateTime creationTime)
    {
        WriteValues(metadataPath, ("创建时间", creationTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
    }

    public static void WriteVideoProperties(string metadataPath, VideoDimensions dimensions)
    {
        var quality = dimensions.Height >= 2160 ? "4K"
            : dimensions.Height >= 1440 ? "2K"
            : $"{dimensions.Height}p";
        var divisor = GreatestCommonDivisor(dimensions.Width, dimensions.Height);
        var aspectRatio = $"{dimensions.Width / divisor}:{dimensions.Height / divisor}";
        WriteValues(metadataPath, ("清晰度", quality), ("宽高比", aspectRatio));
    }

    private static void WriteValues(string metadataPath, params (string Field, string Value)[] valuesToWrite)
    {
        var lines = ReadLines(metadataPath).ToList();
        foreach (var (field, value) in valuesToWrite)
        {
            var fieldLineIndex = lines.FindIndex(line =>
            {
                var columns = line.Split(',', 2);
                return columns.Length == 2 && IsField(columns[0], field);
            });

            if (fieldLineIndex >= 0)
            {
                var columns = lines[fieldLineIndex].Split(',', 2);
                lines[fieldLineIndex] = $"{columns[0]},{value}";
            }
            else
            {
                lines.Add($"{field},{value}");
            }
        }

        File.WriteAllLines(metadataPath, lines, new UTF8Encoding(false));
    }

    private static bool IsField(string value, string field) =>
        string.Equals(value.Trim(), field, StringComparison.OrdinalIgnoreCase) ||
        field switch
        {
            "创建时间" => string.Equals(value.Trim(), "created", StringComparison.OrdinalIgnoreCase),
            "清晰度" => string.Equals(value.Trim(), "quality", StringComparison.OrdinalIgnoreCase),
            "宽高比" => string.Equals(value.Trim(), "aspect_ratio", StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return left;
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
}
