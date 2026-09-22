using System.IO;
using System.Security.Cryptography;
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

        return assets.OrderBy(asset => asset.Record.FolderName).ThenBy(asset => asset.Record.Order).ThenBy(asset => asset.Record.DisplayName).ToArray();
    }

    public AssetRecord ReplaceMedia(
        LibraryAsset asset,
        string videoSourcePath,
        string posterSourcePath,
        IReadOnlyList<string> referenceImageSourcePaths,
        LibraryConfiguration configuration)
    {
        var record = asset.Record;
        var directory = Path.GetDirectoryName(asset.RecordPath)!;
        var mainVideoPath = string.IsNullOrWhiteSpace(videoSourcePath)
            ? Path.Combine(directory, record.LocalPath)
            : CopyReplacement(videoSourcePath, directory, "video", VideoExtensions);
        var localPath = Path.GetFileName(mainVideoPath);
        var posterPath = string.IsNullOrWhiteSpace(posterSourcePath)
            ? record.PosterPath
            : Path.GetFileName(CopyReplacement(posterSourcePath, directory, "poster", ImageExtensions));
        if (!string.IsNullOrWhiteSpace(videoSourcePath) && string.IsNullOrWhiteSpace(posterSourcePath))
        {
            var thumbnail = new VideoThumbnailGenerator().Generate(mainVideoPath, directory, record.Id, configuration);
            posterPath = thumbnail.FileName;
        }

        var references = referenceImageSourcePaths.Count == 0
            ? record.References
            : record.References.Where(reference => !string.Equals(reference.Type, "image", StringComparison.OrdinalIgnoreCase))
            .Concat(referenceImageSourcePaths.Select((sourcePath, index) => new ReferenceResource
                {
                    Type = "image",
                    LocalPath = Path.Combine("references", Path.GetFileName(CopyReplacement(sourcePath, directory, $"reference-{index + 1}", ImageExtensions))).Replace(Path.DirectorySeparatorChar, '/')
                })).ToArray();
        var updatedRecord = new AssetRecord
        {
            Id = record.Id,
            DisplayName = record.DisplayName,
            MediaType = record.MediaType,
            LocalPath = localPath,
            FolderName = record.FolderName,
            CategoryId = record.CategoryId,
            Order = record.Order,
            PosterPath = posterPath,
            Prompt = record.Prompt,
            ContentHash = ComputeHash(mainVideoPath),
            References = references,
            Meta = new AssetMetadata { Created = record.Meta.Created, Quality = record.Meta.Quality, AspectRatio = record.Meta.AspectRatio, Size = new FileInfo(mainVideoPath).Length, Feature = record.Meta.Feature },
            Author = record.Author,
            Tags = record.Tags,
            Rating = record.Rating,
            ReviewStatus = record.ReviewStatus
        };

        ValidateFiles(directory, updatedRecord);
        File.WriteAllText(Path.Combine(directory, "prompt.txt"), updatedRecord.Prompt, new UTF8Encoding(false));
        File.WriteAllText(asset.RecordPath, $"{RecordVariable} {JsonSerializer.Serialize(updatedRecord, JsonOptions)};{Environment.NewLine}", new UTF8Encoding(false));
        return updatedRecord;
    }

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".mkv", ".avi", ".webm" };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" };

    private static string CopyReplacement(string sourcePath, string directory, string prefix, ISet<string> allowedExtensions)
    {
        if (!File.Exists(sourcePath) || !allowedExtensions.Contains(Path.GetExtension(sourcePath)))
        {
            throw new InvalidDataException($"替换文件无效或格式不受支持：{sourcePath}");
        }

        var targetDirectory = prefix.StartsWith("reference", StringComparison.Ordinal) ? Path.Combine(directory, "references") : directory;
        Directory.CreateDirectory(targetDirectory);
        var targetPath = Path.Combine(targetDirectory, $"{prefix}-{Guid.NewGuid():N}{Path.GetExtension(sourcePath)}");
        File.Copy(sourcePath, targetPath);
        return targetPath;
    }

    private static string ComputeHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
    }

    private static void ValidateFiles(string directory, AssetRecord record)
    {
        var paths = new[] { record.LocalPath, record.PosterPath }.Where(path => !string.IsNullOrWhiteSpace(path))
            .Concat(record.References.Select(reference => reference.LocalPath));
        if (paths.Any(path => !File.Exists(Path.Combine(directory, path!)))) throw new InvalidDataException("主视频、封面或参考资源缺失，无法保存。");
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

