using System.IO;
using System.Text;
using System.Text.Json;
using FrameTrace.Editor.Domain;

namespace FrameTrace.Editor.Services;

public sealed class AssetPublisher
{
    private readonly DuplicateAssetDetector duplicateAssetDetector = new();
    private readonly VideoThumbnailGenerator thumbnailGenerator = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    public PublishResult Publish(ImportPreviewRecord previewRecord, LibraryConfiguration configuration, string folderName)
    {
        if (!previewRecord.IsReadyForPublication || previewRecord.ScanResult.MainVideo is null)
        {
            return PublishResult.Failed("记录未通过发布校验。请确认主视频和提示词。");
        }

        var assetsDirectory = configuration.AssetsDirectory;
        Directory.CreateDirectory(assetsDirectory);
        var mainVideo = previewRecord.ScanResult.MainVideo;
        var duplicateCheck = duplicateAssetDetector.Check(mainVideo.FullName, assetsDirectory);
        if (duplicateCheck.IsDuplicate)
        {
            return PublishResult.Skipped($"与已发布记录“{duplicateCheck.ExistingDisplayName}”的主视频内容相同。");
        }

        var assetId = Guid.NewGuid().ToString();
        var physicalDirectoryName = BuildPhysicalDirectoryName(previewRecord.DisplayName, assetId, assetsDirectory);
        var finalDirectory = Path.Combine(assetsDirectory, physicalDirectoryName);
        var temporaryDirectory = Path.Combine(assetsDirectory, $".{physicalDirectoryName}.{Guid.NewGuid():N}.tmp");

        try
        {
            Directory.CreateDirectory(temporaryDirectory);

            var mainVideoTarget = Path.Combine(temporaryDirectory, mainVideo.Name);
            File.Copy(mainVideo.FullName, mainVideoTarget, false);
            var thumbnail = thumbnailGenerator.Generate(mainVideoTarget, temporaryDirectory, assetId, configuration);

            var references = CopyReferenceResources(previewRecord.ScanResult.References, previewRecord.SourceDirectory, temporaryDirectory);
            var prompt = PromptReferenceLinker.ReplaceImagePaths(previewRecord.ConfirmedPrompt!.Trim(), references);
            File.WriteAllText(Path.Combine(temporaryDirectory, "prompt.txt"), prompt, new UTF8Encoding(false));

            var record = new AssetRecord
            {
                Id = assetId,
                DisplayName = previewRecord.DisplayName,
                MediaType = "video",
                LocalPath = mainVideo.Name,
                FolderName = folderName,
                PosterPath = thumbnail.FileName,
                Prompt = prompt,
                ContentHash = duplicateCheck.ContentHash,
                References = references,
                Meta = new AssetMetadata
                {
                    Created = previewRecord.Metadata.Created ?? DateTimeOffset.Now,
                    Quality = previewRecord.Metadata.Quality,
                    AspectRatio = previewRecord.Metadata.AspectRatio,
                    Size = mainVideo.Length,
                    Feature = previewRecord.Metadata.Feature
                }
            };

            WriteRecordFile(temporaryDirectory, record);
            Directory.Move(temporaryDirectory, finalDirectory);
            return PublishResult.Succeeded(assetId, finalDirectory, record, thumbnail.ErrorMessage);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDeleteDirectory(temporaryDirectory);
            return PublishResult.Failed(exception.Message);
        }
    }

    private static IReadOnlyList<ReferenceResource> CopyReferenceResources(
        IReadOnlyList<DetectedReferenceResource> sourceResources,
        string sourceDirectory,
        string temporaryDirectory)
    {
        var result = new List<ReferenceResource>();
        foreach (var sourceResource in sourceResources)
        {
            var targetSubdirectory = sourceResource.Type == "audio" ? "audio" : "references";
            var relativeTargetPath = Path.Combine(targetSubdirectory, sourceResource.RelativePath);
            var targetPath = Path.Combine(temporaryDirectory, relativeTargetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(Path.Combine(sourceDirectory, sourceResource.RelativePath), targetPath, false);
            result.Add(new ReferenceResource
            {
                Type = sourceResource.Type,
                LocalPath = relativeTargetPath.Replace(Path.DirectorySeparatorChar, '/')
            });
        }

        return result;
    }

    private static void WriteRecordFile(string recordDirectory, AssetRecord record)
    {
        var json = JsonSerializer.Serialize(record, JsonOptions);
        var content = $"window.FRAME_TRACE_RECORD = {json};{Environment.NewLine}";
        File.WriteAllText(Path.Combine(recordDirectory, "record.js"), content, new UTF8Encoding(false));
    }

    private static string BuildPhysicalDirectoryName(string displayName, string assetId, string assetsDirectory)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitizedName = string.Concat(displayName.Select(character =>
            invalidCharacters.Contains(character) ? '_' : character)).Trim().TrimEnd('.');

        var baseName = string.IsNullOrWhiteSpace(sanitizedName)
            ? assetId[..8]
            : sanitizedName;
        return Directory.Exists(Path.Combine(assetsDirectory, baseName))
            ? $"{baseName}_{assetId[..8]}"
            : baseName;
    }

    private static void TryDeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}

public sealed class PublishResult
{
    private PublishResult(
        bool isSuccessful,
        bool isSkipped,
        string? assetId,
        string? publishedDirectory,
        AssetRecord? record,
        string? warningMessage,
        string? errorMessage)
    {
        IsSuccessful = isSuccessful;
        IsSkipped = isSkipped;
        AssetId = assetId;
        PublishedDirectory = publishedDirectory;
        Record = record;
        WarningMessage = warningMessage;
        ErrorMessage = errorMessage;
    }

    public bool IsSuccessful { get; }

    public bool IsSkipped { get; }

    public string? AssetId { get; }

    public string? PublishedDirectory { get; }

    public AssetRecord? Record { get; }

    public string? WarningMessage { get; }

    public string? ErrorMessage { get; }

    public static PublishResult Succeeded(string assetId, string publishedDirectory, AssetRecord record, string? warningMessage) =>
        new(true, false, assetId, publishedDirectory, record, warningMessage, null);

    public static PublishResult Skipped(string message) =>
        new(false, true, null, null, null, null, message);

    public static PublishResult Failed(string errorMessage) =>
        new(false, false, null, null, null, null, errorMessage);
}