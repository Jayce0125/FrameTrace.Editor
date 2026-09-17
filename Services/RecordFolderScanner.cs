using System.IO;

namespace FrameTrace.Editor.Services;

public sealed class RecordFolderScanner
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".avi", ".webm"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg"
    };

    public RecordScanResult Scan(string recordDirectory)
    {
        if (!Directory.Exists(recordDirectory))
        {
            return RecordScanResult.Failed("记录文件夹不存在。");
        }

        var directory = new DirectoryInfo(recordDirectory);
        var matchingVideos = directory.EnumerateFiles()
            .Where(file => VideoExtensions.Contains(file.Extension))
            .Where(file => string.Equals(
                Path.GetFileNameWithoutExtension(file.Name),
                directory.Name,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matchingVideos.Length == 0)
        {
            return RecordScanResult.Failed("未找到与记录文件夹同名的主视频。");
        }

        if (matchingVideos.Length > 1)
        {
            return RecordScanResult.Failed("找到多个同名主视频，需要在预览页手动确认。", matchingVideos);
        }

        var promptPath = Path.Combine(recordDirectory, "prompt.txt");
        var prompt = File.Exists(promptPath) ? File.ReadAllText(promptPath) : null;
        var candidatePromptFiles = directory.EnumerateFiles("*.txt")
            .Where(file => !string.Equals(file.Name, "prompt.txt", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var references = directory.EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(file => !file.Attributes.HasFlag(FileAttributes.Hidden))
            .Where(file => !string.Equals(file.FullName, matchingVideos[0].FullName, StringComparison.OrdinalIgnoreCase))
            .Where(file => !IsGeneratedOrDescriptionFile(file.Name))
            .Select(file => CreateReference(file, recordDirectory))
            .Where(reference => reference is not null)
            .Cast<DetectedReferenceResource>()
            .ToArray();

        return RecordScanResult.Valid(matchingVideos[0], prompt, candidatePromptFiles, references);
    }

    private static bool IsGeneratedOrDescriptionFile(string fileName) =>
        string.Equals(fileName, "prompt.txt", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fileName, "record.js", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fileName, "thumbnail.webp", StringComparison.OrdinalIgnoreCase);

    private static DetectedReferenceResource? CreateReference(FileInfo file, string recordDirectory)
    {
        var type = VideoExtensions.Contains(file.Extension) ? "video"
            : ImageExtensions.Contains(file.Extension) ? "image"
            : AudioExtensions.Contains(file.Extension) ? "audio"
            : null;

        return type is null
            ? null
            : new DetectedReferenceResource(type, Path.GetRelativePath(recordDirectory, file.FullName));
    }
}

public sealed class RecordScanResult
{
    private RecordScanResult(
        bool isValid,
        string? errorMessage,
        FileInfo? mainVideo,
        string? prompt,
        IReadOnlyList<FileInfo> candidatePromptFiles,
        IReadOnlyList<FileInfo> ambiguousVideos,
        IReadOnlyList<DetectedReferenceResource> references)
    {
        IsValid = isValid;
        ErrorMessage = errorMessage;
        MainVideo = mainVideo;
        Prompt = prompt;
        CandidatePromptFiles = candidatePromptFiles;
        AmbiguousVideos = ambiguousVideos;
        References = references;
    }

    public bool IsValid { get; }

    public bool IsReadyForPublication =>
        IsValid && !string.IsNullOrWhiteSpace(Prompt);

    public bool RequiresPromptInput =>
        IsValid && string.IsNullOrWhiteSpace(Prompt);

    public string? ErrorMessage { get; }

    public FileInfo? MainVideo { get; }

    public string? Prompt { get; }

    public IReadOnlyList<FileInfo> CandidatePromptFiles { get; }

    public IReadOnlyList<FileInfo> AmbiguousVideos { get; }

    public IReadOnlyList<DetectedReferenceResource> References { get; }

    public static RecordScanResult Valid(
        FileInfo mainVideo,
        string? prompt,
        IReadOnlyList<FileInfo> candidatePromptFiles,
        IReadOnlyList<DetectedReferenceResource> references) =>
        new(true, null, mainVideo, prompt, candidatePromptFiles, [], references);

    public static RecordScanResult Failed(string errorMessage, IReadOnlyList<FileInfo>? ambiguousVideos = null) =>
        new(false, errorMessage, null, null, [], ambiguousVideos ?? [], []);
}

public sealed record DetectedReferenceResource(string Type, string RelativePath);