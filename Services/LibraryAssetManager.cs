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
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
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

                assets.Add(new LibraryAsset(recordPath, record));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                warnings.Add($"已跳过无法读取的记录：{recordPath}（{exception.Message}）");
            }
        }

        return assets.OrderBy(asset => asset.Record.FolderName).ThenBy(asset => asset.Record.DisplayName).ToArray();
    }

    public AssetRecord Save(LibraryAsset asset, AssetEdit edit, LibraryConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(edit.DisplayName) || string.IsNullOrWhiteSpace(edit.FolderName) || string.IsNullOrWhiteSpace(edit.Prompt))
        {
            throw new InvalidDataException("名称、分类和提示词均不能为空。");
        }

        var record = asset.Record;
        var directory = Path.GetDirectoryName(asset.RecordPath)!;
        var mainVideoPath = string.IsNullOrWhiteSpace(edit.VideoSourcePath)
            ? Path.Combine(directory, record.LocalPath)
            : CopyReplacement(edit.VideoSourcePath, directory, "video", VideoExtensions);
        var localPath = Path.GetFileName(mainVideoPath);
        var posterPath = string.IsNullOrWhiteSpace(edit.PosterSourcePath)
            ? record.PosterPath
            : Path.GetFileName(CopyReplacement(edit.PosterSourcePath, directory, "poster", ImageExtensions));
        if (!string.IsNullOrWhiteSpace(edit.VideoSourcePath) && string.IsNullOrWhiteSpace(edit.PosterSourcePath))
        {
            var thumbnail = new VideoThumbnailGenerator().Generate(mainVideoPath, directory, record.Id, configuration);
            posterPath = thumbnail.FileName;
        }

        var references = edit.ReferenceImageSourcePaths.Count == 0
            ? record.References
            : record.References.Where(reference => !string.Equals(reference.Type, "image", StringComparison.OrdinalIgnoreCase))
                .Concat(edit.ReferenceImageSourcePaths.Select((sourcePath, index) => new ReferenceResource
                {
                    Type = "image",
                    LocalPath = Path.Combine("references", Path.GetFileName(CopyReplacement(sourcePath, directory, $"reference-{index + 1}", ImageExtensions))).Replace(Path.DirectorySeparatorChar, '/')
                })).ToArray();
        var updatedRecord = new AssetRecord
        {
            Id = record.Id,
            DisplayName = edit.DisplayName.Trim(),
            MediaType = record.MediaType,
            LocalPath = localPath,
            FolderName = edit.FolderName.Trim(),
            PosterPath = posterPath,
            Prompt = edit.Prompt.Trim(),
            ContentHash = ComputeHash(mainVideoPath),
            References = references,
            Meta = new AssetMetadata { Created = record.Meta.Created, Quality = record.Meta.Quality, AspectRatio = record.Meta.AspectRatio, Size = new FileInfo(mainVideoPath).Length, Feature = record.Meta.Feature },
            Author = string.IsNullOrWhiteSpace(edit.Author) ? null : edit.Author.Trim(),
            Tags = edit.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Rating = ParseRating(edit.Rating),
            ReviewStatus = string.IsNullOrWhiteSpace(edit.ReviewStatus) ? null : edit.ReviewStatus.Trim()
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

    private static int? ParseRating(string rating)
    {
        if (string.IsNullOrWhiteSpace(rating)) return null;
        if (!int.TryParse(rating, out var value) || value is < 1 or > 5) throw new InvalidDataException("评分必须是 1 到 5 的整数。");
        return value;
    }

    private static string ComputeHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void ValidateFiles(string directory, AssetRecord record)
    {
        var paths = new[] { record.LocalPath, record.PosterPath }.Where(path => !string.IsNullOrWhiteSpace(path))
            .Concat(record.References.Select(reference => reference.LocalPath));
        if (paths.Any(path => !File.Exists(Path.Combine(directory, path!)))) throw new InvalidDataException("主视频、封面或参考资源缺失，无法保存。");
    }

    public void Delete(LibraryAsset asset) => Directory.Delete(Path.GetDirectoryName(asset.RecordPath)!, true);
}

public sealed record LibraryAsset(string RecordPath, AssetRecord Record);

public sealed record AssetEdit(string DisplayName, string FolderName, string Prompt, string Author, string Tags, string Rating, string ReviewStatus, string VideoSourcePath, string PosterSourcePath, IReadOnlyList<string> ReferenceImageSourcePaths);