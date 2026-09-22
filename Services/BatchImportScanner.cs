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
            .Where(HasMainVideo)
            .OrderBy(directory => Path.GetRelativePath(batchRoot, directory.FullName), StringComparer.OrdinalIgnoreCase)
            .Select(directory => CreatePreviewRecord(
                directory.FullName,
                Path.GetRelativePath(batchRoot, directory.Parent?.FullName ?? batchRoot)))
            .ToArray();

        if (records.Length == 0)
        {
            return new ImportPreview(batchDirectory, ImportSourceKind.BatchDirectory,
                [ImportPreviewRecord.Failed(batchDirectory, "批次目录中未找到可识别的记录文件夹。记录目录应直接包含与目录同名的视频文件，例如：分类/10/10.mp4")]);
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

    private static bool HasMainVideo(DirectoryInfo directory) => directory.EnumerateFiles()
        .Any(file => string.Equals(Path.GetFileNameWithoutExtension(file.Name), directory.Name, StringComparison.OrdinalIgnoreCase)
            && RecordFolderScanner.IsVideoExtension(file.Extension));
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

    public int NeedsAttentionCount => Records.Count(record => !record.IsReadyForPublication);
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

    public bool IsReadyForPublication =>
        ScanResult.IsValid && Metadata.Warning is null && IsPromptConfirmed && !string.IsNullOrWhiteSpace(ConfirmedPrompt);

    public void ConfirmPrompt(string prompt)
    {
        ConfirmedPrompt = prompt;
        IsPromptConfirmed = true;
    }

    public void ReloadMetadata() => Metadata = MetadataCsvReader.Read(SourceDirectory);

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
}