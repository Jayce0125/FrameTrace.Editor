using System.Globalization;
using System.IO;

namespace FrameTrace.Editor.Services;

public sealed class BatchImportScanner
{
    private readonly RecordFolderScanner recordFolderScanner = new();

    public ImportPreview ScanSingle(string recordDirectory)
    {
        var record = CreatePreviewRecord(recordDirectory, null);
        return new ImportPreview(recordDirectory, ImportSourceKind.SingleRecord, [record]);
    }

    public ImportPreview ScanBatch(string batchDirectory)
    {
        if (!Directory.Exists(batchDirectory))
        {
            return new ImportPreview(
                batchDirectory,
                ImportSourceKind.BatchDirectory,
                [ImportPreviewRecord.Failed(batchDirectory, "批次文件夹不存在。")]);
        }

        var batchRoot = Path.GetFullPath(batchDirectory);
        var records = new DirectoryInfo(batchDirectory)
            .EnumerateDirectories("*", SearchOption.AllDirectories)
            .Where(ContainsVideo)
            .OrderBy(directory => Path.GetRelativePath(batchRoot, directory.FullName), StringComparer.OrdinalIgnoreCase)
            .Select(directory => CreatePreviewRecord(
                directory.FullName,
                Path.GetRelativePath(batchRoot, directory.Parent?.FullName ?? batchRoot)))
            .ToArray();

        if (records.Length == 0)
        {
            return new ImportPreview(batchDirectory, ImportSourceKind.BatchDirectory,
                [ImportPreviewRecord.Failed(batchDirectory, "批次目录中未找到包含视频文件的记录文件夹。记录目录应直接包含与目录同名的视频文件，例如：分类/10/10.mp4")]);
        }

        return new ImportPreview(batchDirectory, ImportSourceKind.BatchDirectory, records);
    }

    private ImportPreviewRecord CreatePreviewRecord(string recordDirectory, string? suggestedCategoryPath)
    {
        var scanResult = recordFolderScanner.Scan(recordDirectory);
        return new ImportPreviewRecord(
            recordDirectory,
            Path.GetFileName(recordDirectory),
            scanResult,
            suggestedCategoryPath);
    }

    private static bool ContainsVideo(DirectoryInfo directory) =>
        directory.EnumerateFiles().Any(file => RecordFolderScanner.IsVideoExtension(file.Extension));
}

public enum ImportSourceKind
{
    SingleRecord,
    BatchDirectory
}

public sealed class ImportPreview
{
    public ImportPreview(string sourceDirectory, ImportSourceKind sourceKind, IReadOnlyList<ImportPreviewRecord> records)
    {
        SourceDirectory = sourceDirectory;
        SourceKind = sourceKind;
        Records = records;
    }

    public string SourceDirectory { get; }

    public ImportSourceKind SourceKind { get; }

    public IReadOnlyList<ImportPreviewRecord> Records { get; }

    public int TotalCount => Records.Count;

    public int ReadyCount => Records.Count(record => record.IsReadyForPublication);

    public int NeedsAttentionCount => Records.Count(record =>
        record.ImportState == ImportState.Pending && !record.IsReadyForPublication);

    public int ImportedCount => Records.Count(record => record.ImportState == ImportState.Imported);

    public int SkippedCount => Records.Count(record => record.ImportState == ImportState.Skipped);

    public int FailedCount => Records.Count(record => record.ImportState == ImportState.Failed);
}

public sealed class ImportPreviewRecord
{
    public ImportPreviewRecord(string sourceDirectory, string displayName, RecordScanResult scanResult, string? suggestedCategoryPath = null)
    {
        SourceDirectory = sourceDirectory;
        DisplayName = displayName;
        ScanResult = scanResult;
        SuggestedCategoryPath = suggestedCategoryPath;
        Metadata = MetadataCsvReader.Read(sourceDirectory);
        ConfirmedPrompt = scanResult.Prompt ?? ReadSingleCandidatePrompt(scanResult.CandidatePromptFiles);
        IsPromptConfirmed = scanResult.Prompt is not null;
    }

    public string SourceDirectory { get; }

    public string DisplayName { get; }

    public RecordScanResult ScanResult { get; }

    public string? SuggestedCategoryPath { get; }

    public ImportMetadata Metadata { get; private set; }

    public string? ConfirmedPrompt { get; private set; }

    public bool IsPromptConfirmed { get; private set; }

    public ImportState ImportState { get; private set; }

    public string? ImportMessage { get; private set; }

    public string? PreflightMessage { get; private set; }

    public string? AspectRatioWarning { get; private set; }

    public bool IsReadyForPublication =>
        ImportState == ImportState.Pending &&
        ScanResult.IsValid &&
        Metadata.Warning is null &&
        PreflightMessage is null &&
        IsPromptConfirmed &&
        !string.IsNullOrWhiteSpace(ConfirmedPrompt);

    public void ConfirmPrompt(string prompt)
    {
        ConfirmedPrompt = prompt;
        IsPromptConfirmed = true;
        ResetImportState();
    }

    public void ReloadMetadata()
    {
        Metadata = MetadataCsvReader.Read(SourceDirectory);
        AspectRatioWarning = null;
        ResetImportState();
    }

    public void NormalizeAspectRatio(LibraryConfiguration configuration)
    {
        var rawAspectRatio = Metadata.AspectRatio;
        if (string.IsNullOrWhiteSpace(rawAspectRatio) && ScanResult.MainVideo is not null)
        {
            var dimensions = VideoMetadataReader.ReadDimensions(ScanResult.MainVideo.FullName, configuration);
            rawAspectRatio = dimensions is null
                ? null
                : $"{dimensions.Width.ToString(CultureInfo.InvariantCulture)}:{dimensions.Height.ToString(CultureInfo.InvariantCulture)}";
        }

        if (string.IsNullOrWhiteSpace(rawAspectRatio))
        {
            AspectRatioWarning = "无法读取宽高比，未能匹配常见比例。";
            return;
        }

        var match = AspectRatioMatcher.Match(rawAspectRatio, configuration.AspectRatioOptions ?? []);
        AspectRatioWarning = match.Warning;
        if (match.Standard is not null)
        {
            Metadata = Metadata with { AspectRatio = match.Standard };
        }
    }

    public void SetMetadataWarning(string warning) =>
        Metadata = Metadata with { Warning = warning };

    public string GetBlockingReason() =>
        ScanResult.ErrorMessage ??
        Metadata.Warning ??
        PreflightMessage ??
        (string.IsNullOrWhiteSpace(ConfirmedPrompt) ? "提示词不能为空。" : "记录未通过发布校验。");

    public void SetPreflightMessage(string? message) => PreflightMessage = message;

    public void MarkImported() => SetImportState(ImportState.Imported, null);

    public void MarkSkipped(string reason) => SetImportState(ImportState.Skipped, reason);

    public void MarkFailed(string reason) => SetImportState(ImportState.Failed, reason);

    public static ImportPreviewRecord Failed(string sourceDirectory, string message) =>
        new(sourceDirectory, Path.GetFileName(sourceDirectory), RecordScanResult.Failed(message));

    private static string? ReadSingleCandidatePrompt(IReadOnlyList<FileInfo> candidatePromptFiles)
    {
        if (candidatePromptFiles.Count != 1)
        {
            return null;
        }

        try
        {
            return PromptTextReader.Read(candidatePromptFiles[0]);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }

    private void ResetImportState() => SetImportState(ImportState.Pending, null);

    private void SetImportState(ImportState importState, string? message)
    {
        ImportState = importState;
        ImportMessage = message;
    }
}

public enum ImportState
{
    Pending,
    Imported,
    Skipped,
    Failed
}