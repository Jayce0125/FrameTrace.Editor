using System.IO;

namespace FrameTrace.Editor.Services;

public sealed class BatchImportScanner
{
    private readonly RecordFolderScanner recordFolderScanner = new();

    public ImportPreview ScanSingle(string recordDirectory)
    {
        var record = CreatePreviewRecord(recordDirectory);
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

        var records = new DirectoryInfo(batchDirectory)
            .EnumerateDirectories()
            .OrderBy(directory => directory.Name, StringComparer.OrdinalIgnoreCase)
            .Select(directory => CreatePreviewRecord(directory.FullName))
            .ToArray();

        return new ImportPreview(batchDirectory, ImportSourceKind.BatchDirectory, records);
    }

    private ImportPreviewRecord CreatePreviewRecord(string recordDirectory)
    {
        var scanResult = recordFolderScanner.Scan(recordDirectory);
        return new ImportPreviewRecord(
            recordDirectory,
            Path.GetFileName(recordDirectory),
            scanResult);
    }
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
    public ImportPreviewRecord(string sourceDirectory, string displayName, RecordScanResult scanResult)
    {
        SourceDirectory = sourceDirectory;
        DisplayName = displayName;
        ScanResult = scanResult;
        Metadata = MetadataCsvReader.Read(sourceDirectory);
        ConfirmedPrompt = scanResult.Prompt ?? ReadSingleCandidatePrompt(scanResult.CandidatePromptFiles);
        IsPromptConfirmed = scanResult.Prompt is not null;
    }

    public string SourceDirectory { get; }

    public string DisplayName { get; }

    public RecordScanResult ScanResult { get; }

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

    private static string? ReadSingleCandidatePrompt(IReadOnlyList<FileInfo> candidatePromptFiles) =>
        candidatePromptFiles.Count == 1 ? File.ReadAllText(candidatePromptFiles[0].FullName) : null;
}