using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using FrameTrace.Editor.Services;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using FolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;
using MessageBox = System.Windows.MessageBox;

namespace FrameTrace.Editor;

public partial class MainWindow : Window
{
    private readonly BatchImportScanner batchImportScanner = new();
    private readonly DuplicateAssetDetector duplicateAssetDetector = new();
    private readonly AssetPublisher assetPublisher = new();
    private readonly TreeViewerIndexPublisher viewerIndexPublisher = new();
    private readonly CategoryCatalogReader categoryCatalogReader = new();
    private readonly LibraryConfiguration configuration;
    private ImportPreview? currentPreview;

    public ObservableCollection<PreviewRecordRow> PreviewRecords { get; } = [];

    public ObservableCollection<string> CategoryPaths { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        configuration = LibraryConfigurationLoader.Load();
        TargetDirectoryTextBlock.Text = configuration.IsConfigured
            ? $"目标素材库：{configuration.AssetsDirectory}"
            : "请先在 config.json 中配置 NAS 目标目录。";
        LoadCategoryPaths();
        LibraryManagerControl.Configure(configuration);
        DataContext = this;
    }

    private void LoadCategoryPaths()
    {
        CategoryPaths.Clear();
        foreach (var path in categoryCatalogReader.ReadPaths(configuration.WebViewerDirectory)) CategoryPaths.Add(path);
    }

    private void SelectSingleRecord_Click(object sender, RoutedEventArgs eventArgs) =>
        SelectAndScan(ImportSourceKind.SingleRecord);

    private void SelectBatch_Click(object sender, RoutedEventArgs eventArgs) =>
        SelectAndScan(ImportSourceKind.BatchDirectory);

    private void SelectAndScan(ImportSourceKind sourceKind)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = sourceKind == ImportSourceKind.SingleRecord ? "选择已整理记录文件夹" : "选择批次父目录",
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        currentPreview = sourceKind == ImportSourceKind.SingleRecord
            ? batchImportScanner.ScanSingle(dialog.SelectedPath)
            : batchImportScanner.ScanBatch(dialog.SelectedPath);
        EnsureMetadataFiles(currentPreview, configuration);
        NormalizeAspectRatios(currentPreview, configuration);
        RunPreflightChecks(currentPreview);

        SourceDirectoryTextBox.Text = currentPreview.SourceDirectory;
        PreviewRecords.Clear();
        foreach (var record in currentPreview.Records)
        {
            PreviewRecords.Add(new PreviewRecordRow(record));
        }

        var suggestedPaths = currentPreview.Records
            .Select(record => record.SuggestedCategoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        CategoryPathComboBox.Text = suggestedPaths.Length == 1 ? suggestedPaths[0]!.Replace(Path.DirectorySeparatorChar, '/') : string.Empty;

        RefreshSummary();
        PromptTextBox.Clear();
        PromptTextBox.IsEnabled = false;
        ConfirmPromptButton.IsEnabled = false;
        UpdatePublishAvailability();
    }

    private void PreviewListView_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (PreviewListView.SelectedItem is not PreviewRecordRow row)
        {
            PromptTextBox.Clear();
            PromptTextBox.IsEnabled = false;
            ConfirmPromptButton.IsEnabled = false;
            return;
        }

        PromptTextBox.Text = row.Record.ConfirmedPrompt ?? string.Empty;
        PromptTextBox.IsEnabled = row.Record.ScanResult.IsValid;
        ConfirmPromptButton.IsEnabled = row.Record.ScanResult.IsValid;
    }

    private void ConfirmPrompt_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (PreviewListView.SelectedItem is not PreviewRecordRow row)
        {
            return;
        }

        row.Record.ConfirmPrompt(PromptTextBox.Text);
        if (currentPreview is not null)
        {
            RunPreflightChecks(currentPreview);
        }
        row.Refresh();
        RefreshSummary();
        UpdatePublishAvailability();
    }

    private void AddMetadata_Click(object sender, RoutedEventArgs eventArgs)
    {
        if ((sender as FrameworkElement)?.DataContext is not PreviewRecordRow row)
        {
            return;
        }

        try
        {
            var templatePath = Path.Combine(AppContext.BaseDirectory, "metadata.csv.example");
            var metadataPath = Path.Combine(row.Record.SourceDirectory, "metadata.csv");
            if (!File.Exists(templatePath))
            {
                throw new FileNotFoundException("未找到 metadata.csv.example 模板文件。", templatePath);
            }

            if (!File.Exists(metadataPath))
            {
                File.Copy(templatePath, metadataPath, false);
            }

            row.Record.ReloadMetadata();
            if (currentPreview is not null)
            {
                NormalizeAspectRatios(currentPreview, configuration);
                RunPreflightChecks(currentPreview);
            }
            row.Refresh();
            RefreshSummary();
            UpdatePublishAvailability();
            Process.Start(new ProcessStartInfo(metadataPath) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show($"无法添加或打开 metadata.csv：{exception.Message}", "操作失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void EnsureMetadataFiles(ImportPreview preview, LibraryConfiguration configuration)
    {
        var templatePath = Path.Combine(AppContext.BaseDirectory, "metadata.csv.example");
        foreach (var record in preview.Records)
        {
            var metadataPath = Path.Combine(record.SourceDirectory, "metadata.csv");
            try
            {
                if (!File.Exists(metadataPath))
                {
                    if (!File.Exists(templatePath))
                    {
                        record.SetMetadataWarning("缺少 metadata.csv，且未找到 metadata.csv.example 模板，无法自动创建。");
                        continue;
                    }

                    File.Copy(templatePath, metadataPath, false);
                }

                if (record.ScanResult.MainVideo is not null)
                {
                    var existingMetadata = MetadataCsvReader.Read(record.SourceDirectory);
                    MetadataCsvWriter.WriteCreatedTime(metadataPath, record.ScanResult.MainVideo.CreationTime);
                    var dimensions = VideoMetadataReader.ReadDimensions(record.ScanResult.MainVideo.FullName, configuration);
                    if (dimensions is not null)
                    {
                        MetadataCsvWriter.WriteVideoProperties(
                            metadataPath,
                            dimensions,
                            configuration.QualityOptions,
                            string.IsNullOrWhiteSpace(existingMetadata.Quality),
                            string.IsNullOrWhiteSpace(existingMetadata.AspectRatio));
                    }
                }

                record.ReloadMetadata();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                record.SetMetadataWarning($"无法写入 metadata.csv：{exception.Message}");
            }
        }
    }

    private static void NormalizeAspectRatios(ImportPreview preview, LibraryConfiguration configuration)
    {
        foreach (var record in preview.Records)
        {
            record.NormalizeAspectRatio(configuration);
        }
    }

    private void ManageLibrary_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (!configuration.IsConfigured)
        {
            MessageBox.Show("请先在程序目录的 config.json 中配置 assets_directory 和 web_viewer_directory。", "无法打开管理", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        MainTabControl.SelectedItem = ManagementTabItem;
        LibraryManagerControl.ReloadData();
    }

    private void Publish_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (currentPreview is null)
        {
            return;
        }

        if (!configuration.IsConfigured)
        {
            MessageBox.Show("请先在程序目录的 config.json 中配置 assets_directory 和 web_viewer_directory。", "无法发布", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var categoryPath = CategoryPathComboBox.Text.Trim();
        var hasRecordsWithoutSuggestedCategory = currentPreview.Records.Any(record =>
            record.IsReadyForPublication && string.IsNullOrWhiteSpace(record.SuggestedCategoryPath));
        if (hasRecordsWithoutSuggestedCategory && string.IsNullOrWhiteSpace(categoryPath))
        {
            MessageBox.Show("请选择或填写分类路径。", "无法发布", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!LibraryPublishLock.TryAcquire(configuration.AssetsDirectory, out var publishLock, out var lockError))
        {
            MessageBox.Show(lockError, "无法发布", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        using (publishLock)
        {
            var readyRecords = currentPreview.Records.Where(record => record.IsReadyForPublication).ToArray();
            var succeeded = 0;
            var skipped = 0;
            var failures = new List<string>();
            var warnings = new List<string>();
            var publishedAssets = new List<PublishResult>();

            foreach (var record in readyRecords)
            {
                PublishResult result;
                try
                {
                    var recordCategoryPath = string.IsNullOrWhiteSpace(record.SuggestedCategoryPath)
                        ? categoryPath
                        : record.SuggestedCategoryPath;
                    result = assetPublisher.Publish(record, configuration, recordCategoryPath!.Replace(" / ", "/", StringComparison.Ordinal).Replace(Path.DirectorySeparatorChar, '/'));
                }
                catch (Exception exception)
                {
                    var error = $"发布时发生未预期错误：{exception.Message}";
                    record.MarkFailed(error);
                    failures.Add($"{record.DisplayName}: {error}");
                    continue;
                }

                if (result.IsSuccessful && result.Record is not null)
                {
                    succeeded++;
                    record.MarkImported();
                    publishedAssets.Add(result);
                    if (!string.IsNullOrWhiteSpace(result.WarningMessage))
                    {
                        warnings.Add($"{record.DisplayName}: 封面生成失败：{result.WarningMessage}");
                    }
                    if (!string.IsNullOrWhiteSpace(record.AspectRatioWarning))
                    {
                        warnings.Add($"{record.DisplayName}: {record.AspectRatioWarning}");
                    }
                }
                else if (result.IsSkipped)
                {
                    skipped++;
                    record.MarkSkipped(result.SkipReason!);
                }
                else
                {
                    var error = result.ErrorMessage ?? "发布失败。";
                    record.MarkFailed(error);
                    failures.Add($"{record.DisplayName}: {error}");
                }
            }

            if (publishedAssets.Count > 0)
            {
                try
                {
                    viewerIndexPublisher.Publish(
                        configuration.AssetsDirectory,
                        configuration.WebViewerDirectory,
                        configuration.ProjectName,
                        configuration.ViewerPageSize);
                }
                catch (Exception exception)
                {
                    failures.Add($"展示索引未更新: {exception.Message}。已入库的素材不会丢失，可在修复网页数据后重新发布。 ");
                }
            }

            LoadCategoryPaths();
            RefreshPreviewRows();
            RefreshSummary();
            UpdatePublishAvailability();

            var message = $"发布完成。成功 {succeeded} 条，跳过 {skipped} 条，失败 {failures.Count} 条，警告 {warnings.Count} 条。";
            if (failures.Count > 0)
            {
                message += $"{Environment.NewLine}{Environment.NewLine}{string.Join(Environment.NewLine, failures)}";
            }
            if (warnings.Count > 0)
            {
                message += $"{Environment.NewLine}{Environment.NewLine}{string.Join(Environment.NewLine, warnings)}";
            }

            MessageBox.Show(message, "发布结果", MessageBoxButton.OK,
                failures.Count == 0 && warnings.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
    }

    private void RefreshSummary()
    {
        if (currentPreview is null)
        {
            return;
        }

        var hasImportResults = currentPreview.ImportedCount > 0 ||
            currentPreview.SkippedCount > 0 ||
            currentPreview.FailedCount > 0;
        SummaryTextBlock.Text = hasImportResults
            ? $"共识别 {currentPreview.TotalCount} 条记录；已导入 {currentPreview.ImportedCount} 条；已跳过 {currentPreview.SkippedCount} 条；导入失败 {currentPreview.FailedCount} 条；需处理 {currentPreview.NeedsAttentionCount} 条。"
            : $"共识别 {currentPreview.TotalCount} 条记录；可发布 {currentPreview.ReadyCount} 条；需处理 {currentPreview.NeedsAttentionCount} 条。";
    }

    private void RunPreflightChecks(ImportPreview preview)
    {
        foreach (var record in preview.Records)
        {
            record.SetPreflightMessage(null);
            if (!record.ScanResult.IsValid ||
                !configuration.IsConfigured ||
                record.ScanResult.MainVideo is null)
            {
                continue;
            }

            try
            {
                var duplicateCheck = duplicateAssetDetector.Check(
                    record.ScanResult.MainVideo.FullName,
                    record.DisplayName,
                    configuration.AssetsDirectory);
                if (duplicateCheck.IsDuplicate)
                {
                    record.SetPreflightMessage($"与已发布记录“{duplicateCheck.ExistingDisplayName}”的主视频内容相同。");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                record.SetPreflightMessage($"无法完成重复素材检查：{exception.Message}");
            }
        }
    }

    private void UpdatePublishAvailability()
    {
        PublishButton.IsEnabled = currentPreview?.ReadyCount > 0;
    }

    private void RefreshPreviewRows()
    {
        foreach (var row in PreviewRecords)
        {
            row.Refresh();
        }
    }
}

public sealed class PreviewRecordRow : System.ComponentModel.INotifyPropertyChanged
{
    public PreviewRecordRow(ImportPreviewRecord record)
    {
        Record = record;
        Refresh();
    }

    public ImportPreviewRecord Record { get; }

    public void Refresh()
    {
        DisplayName = Record.DisplayName;
        CategoryPath = string.IsNullOrWhiteSpace(Record.SuggestedCategoryPath)
            ? "未指定"
            : Record.SuggestedCategoryPath.Replace(Path.DirectorySeparatorChar, '/');
        MainVideoName = Record.ScanResult.MainVideo?.Name ?? "未识别";
        PromptStatus = Record.ScanResult.Prompt switch
        {
            null when Record.ScanResult.CandidatePromptFiles.Count > 0 => "需确认候选文件",
            null => "需要手动填写",
            _ when string.IsNullOrWhiteSpace(Record.ScanResult.Prompt) => "需要手动填写",
            _ => Record.ScanResult.PromptSource is not null
                ? $"已读取 {Record.ScanResult.PromptSource}"
                : "已读取提示词"
        };
        Status = Record.ImportState switch
        {
            ImportState.Imported => "已导入",
            ImportState.Skipped => "已跳过",
            ImportState.Failed => "导入失败",
            _ when Record.IsReadyForPublication => "待导入",
            _ => "需要处理"
        };
        Details = Record.ImportState switch
        {
            ImportState.Skipped => $"跳过原因：{Record.ImportMessage ?? Record.GetBlockingReason()}",
            ImportState.Failed => $"失败原因：{Record.ImportMessage ?? "发布失败。"}",
            _ => Record.ScanResult.ErrorMessage ??
                Record.Metadata.Warning ??
                Record.PreflightMessage ??
                (Record.IsReadyForPublication ? "校验通过" : "提示词不能为空")
        };
        DetailsWithoutWarning = Details;
        AspectRatioWarningText = string.Empty;
        if (!string.IsNullOrWhiteSpace(Record.AspectRatioWarning))
        {
            AspectRatioWarningText = $"警告：{Record.AspectRatioWarning}";
            Details += $"；{AspectRatioWarningText}";
        }
        ReferenceCount = Record.ScanResult.References.Count.ToString();
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(null));
    }

    public bool IsMetadataMissing => Record.Metadata.Warning == "缺少必要的 metadata.csv 文件。";

    public string DetailsWithoutWarning { get; private set; } = string.Empty;

    public string AspectRatioWarningText { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public string CategoryPath { get; private set; } = string.Empty;

    public string MainVideoName { get; private set; } = string.Empty;

    public string ReferenceCount { get; private set; } = string.Empty;

    public string PromptStatus { get; private set; } = string.Empty;

    public string Status { get; private set; } = string.Empty;

    public string Details { get; private set; } = string.Empty;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
