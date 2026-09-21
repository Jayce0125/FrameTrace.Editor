using System.IO;
using System.Text;
using System.Text.Json;
using FrameTrace.Editor.Domain;

namespace FrameTrace.Editor.Services;

public sealed class ViewerIndexPublisher
{
    private const string IndexVariable = "window.VIEWER_INDEX =";
    private const string FolderVariable = "window.VIEWER_FOLDER_DATA";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = new SnakeCaseNamingPolicy(),
        WriteIndented = true
    };

    public void Publish(
        string webViewerDirectory,
        string projectName,
        string folderName,
        IReadOnlyList<PublishResult> publishedAssets,
        int pageSize)
    {
        var newRecords = publishedAssets
            .Where(result => result.Record is not null && result.PublishedDirectory is not null)
            .Select(result => CreateViewerRecord(result.Record!, result.PublishedDirectory!, webViewerDirectory))
            .ToArray();
        var dataDirectory = Path.Combine(webViewerDirectory, "data");
        var folderKey = ToFolderKey(folderName);
        var folderDataDirectory = Path.Combine(dataDirectory, folderKey);
        Directory.CreateDirectory(folderDataDirectory);

        var indexPath = Path.Combine(dataDirectory, "index.js");
        var index = ReadIndex(indexPath, projectName);
        var existingRecords = ReadFolderRecords(index, folderKey, folderDataDirectory);
        var mergedRecords = existingRecords.Concat(newRecords)
            .GroupBy(record => record.LocalPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (mergedRecords.Length == 0)
        {
            return;
        }

        var version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var pages = mergedRecords.Chunk(Math.Max(1, pageSize)).ToArray();
        var pageFiles = new List<string>();
        for (var pageIndex = 0; pageIndex < pages.Length; pageIndex++)
        {
            var pageName = $"page-{pageIndex + 1}.js";
            WriteAtomically(
                Path.Combine(folderDataDirectory, pageName),
                BuildFolderScript(folderName, version, pages[pageIndex]));
            pageFiles.Add($"data/{folderKey}/{pageName}?v={version}");
        }

        var folderTree = index.FolderTree
            .Where(folder => !string.Equals(folder.Name, folderName, StringComparison.OrdinalIgnoreCase))
            .Append(new ViewerFolder(folderKey, folderName, mergedRecords.Length, []))
            .OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        index.FolderFiles[folderKey] = pageFiles[0];
        var folderPages = index.FolderPages is null
            ? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, List<string>>(index.FolderPages, StringComparer.OrdinalIgnoreCase);
        folderPages[folderKey] = pageFiles;
        index = index with
        {
            Version = version,
            ProjectName = projectName,
            FolderTree = folderTree,
            TotalAssets = folderTree.Sum(folder => folder.Count),
            FolderPages = folderPages
        };

        WriteAtomically(indexPath, $"{IndexVariable} {JsonSerializer.Serialize(index, JsonOptions)};{Environment.NewLine}");
    }

    public void Rebuild(string assetsDirectory, string webViewerDirectory, string projectName, int pageSize)
    {
        var recordsByFolder = new Dictionary<string, List<(AssetRecord Record, string Directory)>>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(assetsDirectory))
        {
            foreach (var recordPath in Directory.EnumerateFiles(assetsDirectory, "record.js", SearchOption.AllDirectories))
            {
                try
                {
                    var record = ReadRecord(recordPath);
                    if (record is null)
                    {
                        continue;
                    }

                    var folderRecords = recordsByFolder.GetValueOrDefault(record.FolderName) ?? [];
                    folderRecords.Add((record, Path.GetDirectoryName(recordPath)!));
                    recordsByFolder[record.FolderName] = folderRecords;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    continue;
                }
            }
        }

        var dataDirectory = Path.Combine(webViewerDirectory, "data");
        Directory.CreateDirectory(dataDirectory);
        foreach (var directory in Directory.EnumerateDirectories(dataDirectory))
        {
            Directory.Delete(directory, true);
        }

        var version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var folderFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var folderPages = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var folderTree = new List<ViewerFolder>();
        foreach (var (folderName, records) in recordsByFolder.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            var folderKey = ToFolderKey(folderName);
            var folderDataDirectory = Path.Combine(dataDirectory, folderKey);
            Directory.CreateDirectory(folderDataDirectory);
            var pages = records.Select(entry => CreateViewerRecord(entry.Record, entry.Directory, webViewerDirectory))
                .Chunk(Math.Max(1, pageSize))
                .ToArray();
            var pageFiles = new List<string>();
            for (var pageIndex = 0; pageIndex < pages.Length; pageIndex++)
            {
                var pageName = $"page-{pageIndex + 1}.js";
                WriteAtomically(Path.Combine(folderDataDirectory, pageName), BuildFolderScript(folderName, version, pages[pageIndex]));
                pageFiles.Add($"data/{folderKey}/{pageName}?v={version}");
            }

            folderFiles[folderKey] = pageFiles[0];
            folderPages[folderKey] = pageFiles;
            folderTree.Add(new ViewerFolder(folderKey, folderName, records.Count, []));
        }

        var index = new ViewerIndex(version, projectName, folderTree.Sum(folder => folder.Count), folderTree, folderFiles, folderPages);
        WriteAtomically(Path.Combine(dataDirectory, "index.js"), $"{IndexVariable} {JsonSerializer.Serialize(index, JsonOptions)};{Environment.NewLine}");
    }

    public void Update(string webViewerDirectory, string projectName, string previousFolderName, AssetRecord updatedRecord, string assetDirectory, int pageSize)
    {
        var index = ReadIndex(Path.Combine(webViewerDirectory, "data", "index.js"), projectName);
        var replacements = new Dictionary<string, AssetRecord>(StringComparer.Ordinal) { [updatedRecord.Id] = CreateViewerRecord(updatedRecord, assetDirectory, webViewerDirectory) };
        RewriteFolders(webViewerDirectory, projectName, index, [previousFolderName, updatedRecord.FolderName], replacements, pageSize);
    }

    public void Remove(string webViewerDirectory, string projectName, AssetRecord record, int pageSize)
    {
        var index = ReadIndex(Path.Combine(webViewerDirectory, "data", "index.js"), projectName);
        RewriteFolders(webViewerDirectory, projectName, index, [record.FolderName], new Dictionary<string, AssetRecord>(StringComparer.Ordinal) { [record.Id] = null! }, pageSize);
    }

    private void RewriteFolders(string webViewerDirectory, string projectName, ViewerIndex index, IEnumerable<string> folders, IReadOnlyDictionary<string, AssetRecord> replacements, int pageSize)
    {
        var dataDirectory = Path.Combine(webViewerDirectory, "data");
        var version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var folderPages = new Dictionary<string, List<string>>(index.FolderPages ?? [], StringComparer.OrdinalIgnoreCase);
        var folderFiles = new Dictionary<string, string>(index.FolderFiles, StringComparer.OrdinalIgnoreCase);
        var tree = index.FolderTree.ToDictionary(folder => folder.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var folderName in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var folderKey = ToFolderKey(folderName);
            var folderDirectory = Path.Combine(dataDirectory, folderKey);
            var records = ReadFolderRecords(index, folderKey, folderDirectory).Where(record => !replacements.ContainsKey(record.Id)).ToList();
            records.AddRange(replacements.Values.Where(record => record is not null && string.Equals(record.FolderName, folderName, StringComparison.OrdinalIgnoreCase)));
            if (records.Count == 0)
            {
                if (Directory.Exists(folderDirectory)) Directory.Delete(folderDirectory, true);
                folderPages.Remove(folderKey); folderFiles.Remove(folderKey); tree.Remove(folderKey);
                continue;
            }

            Directory.CreateDirectory(folderDirectory);
            var pages = records.Chunk(Math.Max(1, pageSize)).ToArray();
            var pageFiles = new List<string>();
            for (var pageIndex = 0; pageIndex < pages.Length; pageIndex++)
            {
                var pageName = $"page-{pageIndex + 1}.js";
                WriteAtomically(Path.Combine(folderDirectory, pageName), BuildFolderScript(folderName, version, pages[pageIndex]));
                pageFiles.Add($"data/{folderKey}/{pageName}?v={version}");
            }
            foreach (var stalePage in Directory.EnumerateFiles(folderDirectory, "page-*.js").Where(file => !pageFiles.Any(page => string.Equals(Path.GetFileName(page.Split('?', 2)[0]), Path.GetFileName(file), StringComparison.OrdinalIgnoreCase)))) File.Delete(stalePage);
            folderPages[folderKey] = pageFiles; folderFiles[folderKey] = pageFiles[0]; tree[folderKey] = new ViewerFolder(folderKey, folderName, records.Count, []);
        }
        var treeList = tree.Values.OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var updatedIndex = new ViewerIndex(version, projectName, treeList.Sum(folder => folder.Count), treeList, folderFiles, folderPages);
        WriteAtomically(Path.Combine(dataDirectory, "index.js"), $"{IndexVariable} {JsonSerializer.Serialize(updatedIndex, JsonOptions)};{Environment.NewLine}");
    }

    private static string BuildFolderScript(string folderName, string version, IReadOnlyList<AssetRecord> records)
    {
        var document = new ViewerFolderData(folderName, version, records.Count, records);
        var json = JsonSerializer.Serialize(document, JsonOptions);
        return $"{FolderVariable} = {FolderVariable} || {{}};{Environment.NewLine}" +
            $"{FolderVariable}[{JsonSerializer.Serialize(ToFolderKey(folderName))}] = {json};{Environment.NewLine}";
    }

    private static AssetRecord CreateViewerRecord(AssetRecord record, string assetDirectory, string webViewerDirectory)
    {
        var assetDirectoryPath = Path.GetRelativePath(webViewerDirectory, assetDirectory).Replace(Path.DirectorySeparatorChar, '/');
        var pathPrefix = assetDirectoryPath.EndsWith('/') ? assetDirectoryPath : $"{assetDirectoryPath}/";
        return new AssetRecord
        {
            Id = record.Id,
            DisplayName = record.DisplayName,
            MediaType = record.MediaType,
            LocalPath = $"{pathPrefix}{record.LocalPath}",
            FolderName = record.FolderName,
            PosterPath = record.PosterPath is null ? null : $"{pathPrefix}{record.PosterPath}",
            Prompt = record.Prompt,
            References = record.References.Select(reference => new ReferenceResource
            {
                Type = reference.Type,
                LocalPath = $"{pathPrefix}{reference.LocalPath}"
            }).ToArray(),
            Meta = record.Meta,
            Author = record.Author,
            Tags = record.Tags,
            Rating = record.Rating,
            ReviewStatus = record.ReviewStatus
        };
    }

    private static ViewerIndex ReadIndex(string indexPath, string projectName)
    {
        if (!File.Exists(indexPath))
        {
            return new ViewerIndex("", projectName, 0, [], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        var json = ExtractAssignedJson(File.ReadAllText(indexPath), IndexVariable);
        return JsonSerializer.Deserialize<ViewerIndex>(json, JsonOptions)
            ?? new ViewerIndex("", projectName, 0, [], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<AssetRecord> ReadFolderRecords(ViewerIndex index, string folderKey, string folderDataDirectory)
    {
        var pageFiles = index.FolderPages?.GetValueOrDefault(folderKey);
        if (pageFiles is null || pageFiles.Count == 0)
        {
            pageFiles = [Path.Combine(folderDataDirectory, "page-1.js")];
        }

        var records = new List<AssetRecord>();
        foreach (var pageFile in pageFiles)
        {
            var relativePath = pageFile.Split('?', 2)[0].Trim();
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            relativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
            var filePath = Path.IsPathRooted(relativePath)
                ? relativePath
                : Path.Combine(Path.GetDirectoryName(folderDataDirectory)!, "..", relativePath);
            filePath = Path.GetFullPath(filePath);
            if (!File.Exists(filePath))
            {
                continue;
            }

            var script = File.ReadAllText(filePath);
            var marker = $"{FolderVariable}[";
            var start = script.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }

            var assignment = script.IndexOf('=', start);
            if (assignment < 0)
            {
                continue;
            }

            var json = script[(assignment + 1)..].Trim().TrimEnd(';');
            var document = JsonSerializer.Deserialize<ViewerFolderData>(json, JsonOptions);
            records.AddRange(document?.Assets ?? []);
        }

        return records;
    }

    private static string ExtractAssignedJson(string script, string variableName)
    {
        var start = script.IndexOf(variableName, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidDataException($"未找到索引变量 {variableName}。");
        }

        return script[(start + variableName.Length)..].Trim().TrimEnd(';');
    }

    private static AssetRecord? ReadRecord(string recordPath)
    {
        const string recordVariable = "window.FRAME_TRACE_RECORD =";
        var script = File.ReadAllText(recordPath);
        var json = ExtractAssignedJson(script, recordVariable);
        return JsonSerializer.Deserialize<AssetRecord>(json, JsonOptions);
    }

    private static void WriteAtomically(string destinationPath, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, content, new UTF8Encoding(false));
        File.Move(temporaryPath, destinationPath, true);
    }

    private static string ToFolderKey(string folderName) =>
        string.Concat(folderName.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
}

public sealed record ViewerFolder(string Id, string Name, int Count, IReadOnlyList<ViewerFolder> Children);

public sealed record ViewerFolderData(string FolderName, string Version, int TotalAssets, IReadOnlyList<AssetRecord> Assets);

public sealed record ViewerIndex(
    string Version,
    string ProjectName,
    int TotalAssets,
    List<ViewerFolder> FolderTree,
    Dictionary<string, string> FolderFiles,
    Dictionary<string, List<string>>? FolderPages = null);