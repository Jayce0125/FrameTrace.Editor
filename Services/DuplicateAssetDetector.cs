using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using FrameTrace.Editor.Domain;

namespace FrameTrace.Editor.Services;

public sealed class DuplicateAssetDetector
{
    private const string RecordVariable = "window.FRAME_TRACE_RECORD =";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = new SnakeCaseNamingPolicy()
    };

    public DuplicateCheckResult Check(string sourceVideoPath, string sourceDisplayName, string assetsDirectory)
    {
        var sourceHash = CalculateHash(sourceVideoPath);
        if (!Directory.Exists(assetsDirectory))
        {
            return DuplicateCheckResult.Unique(sourceHash);
        }

        foreach (var recordPath in Directory.EnumerateFiles(assetsDirectory, "record.js", SearchOption.AllDirectories))
        {
            var record = TryReadRecord(recordPath);
            if (record is null)
            {
                continue;
            }

            if (!string.Equals(sourceDisplayName, record.DisplayName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var existingVideoPath = ResolveVideoPath(recordPath, record.LocalPath, assetsDirectory);
            if (!File.Exists(existingVideoPath))
            {
                continue;
            }

            var existingHash = CalculateHash(existingVideoPath);
            if (string.Equals(sourceHash, existingHash, StringComparison.OrdinalIgnoreCase))
            {
                return DuplicateCheckResult.Duplicate(sourceHash, record.DisplayName);
            }
        }

        return DuplicateCheckResult.Unique(sourceHash);
    }

    private static string ResolveVideoPath(string recordPath, string localPath, string assetsDirectory)
    {
        var normalizedPath = localPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var recordDirectory = Path.GetDirectoryName(recordPath)!;
        var relativeToRecordPath = Path.GetFullPath(Path.Combine(recordDirectory, normalizedPath));
        if (File.Exists(relativeToRecordPath))
        {
            return relativeToRecordPath;
        }

        const string webViewerAssetsPrefix = "../assets/";
        if (localPath.StartsWith(webViewerAssetsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(Path.Combine(
                assetsDirectory,
                localPath[webViewerAssetsPrefix.Length..]
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar)));
        }

        return relativeToRecordPath;
    }

    private static AssetRecord? TryReadRecord(string recordPath)
    {
        var script = File.ReadAllText(recordPath);
        var start = script.IndexOf(RecordVariable, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var json = script[(start + RecordVariable.Length)..].Trim().TrimEnd(';');
        return JsonSerializer.Deserialize<AssetRecord>(json, JsonOptions);
    }

    private static string CalculateHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var md5 = MD5.Create();
        return Convert.ToHexString(md5.ComputeHash(stream));
    }
}

public sealed record DuplicateCheckResult(bool IsDuplicate, string ContentHash, string? ExistingDisplayName)
{
    public static DuplicateCheckResult Unique(string contentHash) => new(false, contentHash, null);

    public static DuplicateCheckResult Duplicate(string contentHash, string displayName) => new(true, contentHash, displayName);
}