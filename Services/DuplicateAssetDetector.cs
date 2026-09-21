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

    public DuplicateCheckResult Check(string sourceVideoPath, string assetsDirectory)
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

            var existingHash = record.ContentHash;
            if (string.IsNullOrWhiteSpace(existingHash))
            {
                var existingVideoPath = Path.Combine(Path.GetDirectoryName(recordPath)!, record.LocalPath);
                if (!File.Exists(existingVideoPath))
                {
                    continue;
                }

                existingHash = CalculateHash(existingVideoPath);
            }

            if (string.Equals(sourceHash, existingHash, StringComparison.OrdinalIgnoreCase))
            {
                return DuplicateCheckResult.Duplicate(sourceHash, record.DisplayName);
            }
        }

        return DuplicateCheckResult.Unique(sourceHash);
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
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(stream));
    }
}

public sealed record DuplicateCheckResult(bool IsDuplicate, string ContentHash, string? ExistingDisplayName)
{
    public static DuplicateCheckResult Unique(string contentHash) => new(false, contentHash, null);

    public static DuplicateCheckResult Duplicate(string contentHash, string displayName) => new(true, contentHash, displayName);
}