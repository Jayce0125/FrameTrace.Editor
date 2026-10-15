using System.IO;
using System.Text;
using System.Text.Json;
using FrameTrace.Editor.Domain;

namespace FrameTrace.Editor.Services;

public sealed class LibraryAssetManager
{
    private const string RecordVariable = "window.FRAME_TRACE_RECORD =";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = new SnakeCaseNamingPolicy(),
        WriteIndented = true
    };

    public IReadOnlyList<LibraryAsset> Load(string assetsDirectory, out List<string> warnings)
    {
        warnings = [];
        if (!Directory.Exists(assetsDirectory))
        {
            return [];
        }

        var assets = new List<LibraryAsset>();
        foreach (var recordPath in Directory.EnumerateFiles(assetsDirectory, "record.js", SearchOption.AllDirectories))
        {
            try
            {
                var script = File.ReadAllText(recordPath);
                var start = script.IndexOf(RecordVariable, StringComparison.Ordinal);
                if (start < 0)
                {
                    warnings.Add($"已跳过格式不正确的记录：{recordPath}");
                    continue;
                }

                var json = script[(start + RecordVariable.Length)..].Trim().TrimEnd(';');
                var record = JsonSerializer.Deserialize<AssetRecord>(json, JsonOptions);
                if (record is null)
                {
                    warnings.Add($"已跳过无法读取的记录：{recordPath}");
                    continue;
                }

                record.SourcePath = recordPath;
                assets.Add(new LibraryAsset(recordPath, record));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                warnings.Add($"已跳过无法读取的记录：{recordPath}（{exception.Message}）");
            }
        }

        return assets.OrderBy(asset => asset.Record.FolderName).ThenBy(asset => asset.Record.DisplayName).ToArray();
    }

    public AssetRecord UpdateTextAndMetadata(
        LibraryAsset asset,
        string displayName,
        string prompt,
        DateTimeOffset? created,
        string? quality,
        string? aspectRatio,
        string? feature,
        string? skills)
    {
        var record = asset.Record;
        var updatedRecord = new AssetRecord
        {
            Id = record.Id,
            DisplayName = displayName,
            MediaType = record.MediaType,
            LocalPath = record.LocalPath,
            FolderName = record.FolderName,
            CategoryId = record.CategoryId,
            PosterPath = record.PosterPath,
            Prompt = prompt,
            ContentHash = record.ContentHash,
            References = record.References,
            Meta = new AssetMetadata
            {
                Created = created,
                Quality = quality,
                AspectRatio = aspectRatio,
                Size = record.Meta.Size,
                Feature = feature,
                Skills = skills
            },
            Author = record.Author,
            Tags = record.Tags,
            Rating = record.Rating,
            ReviewStatus = record.ReviewStatus,
            SourcePath = asset.RecordPath
        };

        var directory = Path.GetDirectoryName(asset.RecordPath)!;
        File.WriteAllText(Path.Combine(directory, "prompt.txt"), prompt, new UTF8Encoding(false));
        File.WriteAllText(asset.RecordPath, $"{RecordVariable} {JsonSerializer.Serialize(updatedRecord, JsonOptions)};{Environment.NewLine}", new UTF8Encoding(false));
        return updatedRecord;
    }

    public void Delete(LibraryAsset asset) => Directory.Delete(Path.GetDirectoryName(asset.RecordPath)!, true);

    public void Clear(string assetsDirectory)
    {
        if (!Directory.Exists(assetsDirectory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFileSystemEntries(assetsDirectory).ToArray())
        {
            if (string.Equals(Path.GetFileName(path), ".frametrace.publish.lock", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
            else
            {
                File.Delete(path);
            }
        }
    }
}

public sealed record LibraryAsset(string RecordPath, AssetRecord Record);
