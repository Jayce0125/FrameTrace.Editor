using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using FrameTrace.Editor.Services;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;

namespace FrameTrace.Editor;

public partial class MainWindow : Window
{
    private readonly BatchImportScanner batchImportScanner = new();
    private readonly AssetPublisher assetPublisher = new();
    private readonly ViewerIndexPublisher viewerIndexPublisher = new();
    private readonly LibraryConfiguration configuration;
    private ImportPreview? currentPreview;

    public ObservableCollection<PreviewRecordRow> PreviewRecords { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        configuration = LibraryConfigurationLoader.Load();
        TargetDirectoryTextBlock.Text = configuration.IsConfigured
            ? $"目标素材库：{configuration.AssetsDirectory}"
            : "请先在 config.json 中配置 NAS 目标目录。";
        DataContext = this;
    }

    private void SelectSingleRecord_Click(object sender, RoutedEventArgs eventArgs) =>
        SelectAndScan(ImportSourceKind.SingleRecord);

    private void SelectBatch_Click(object sender, RoutedEventArgs eventArgs) =>
        SelectAndScan(ImportSourceKind.BatchDirectory);

    private void SelectAndScan(ImportSourceKind sourceKind)
    {
        var dialog = new OpenFolderDialog
        {
            Title = sourceKind == ImportSourceKind.SingleRecord ? "选择已整理记录文件夹" : "选择批次父目录"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        currentPreview = sourceKind == ImportSourceKind.SingleRecord
            ? batchImportScanner.ScanSingle(dialog.FolderName)
            : batchImportScanner.ScanBatch(dialog.FolderName);

        SourceDirectoryTextBox.Text = currentPreview.SourceDirectory;
        PreviewRecords.Clear();
        foreach (var record in currentPreview.Records)
        {
            PreviewRecords.Add(new PreviewRecordRow(record));
        }

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

            File.Copy(templatePath, metadataPath, false);
            row.Record.ReloadMetadata();
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

    private void ManageLibrary_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (!configuration.IsConfigured)
        {
            MessageBox.Show("请先在程序目录的 config.json 中配置 assets_directory 和 web_viewer_directory。", "无法打开管理", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        new LibraryManagerWindow(configuration) { Owner = this }.ShowDialog();
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

        var folderName = FolderNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(folderName))
        {
            MessageBox.Show("请填写单级分类名称。", "无法发布", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            var skipped = currentPreview.TotalCount - readyRecords.Length;
            var failures = new List<string>();
            var warnings = new List<string>();
            var publishedAssets = new List<PublishResult>();
            foreach (var record in readyRecords)
            {
                PublishResult result;
                try
                {
                    result = assetPublisher.Publish(record, configuration, folderName);
                }
                catch (Exception exception)
                {
                    failures.Add($"{record.DisplayName}: 发布时发生未预期错误：{exception.Message}");
                    continue;
                }

                if (result.IsSuccessful && result.Record is not null)
                {
                    succeeded++;
                    publishedAssets.Add(result);
                    if (!string.IsNullOrWhiteSpace(result.WarningMessage))
                    {
                        warnings.Add($"{record.DisplayName}: 封面生成失败：{result.WarningMessage}");
                    }
                }
                else if (result.IsSkipped)
                {
                    skipped++;
                }
                else
                {
                    failures.Add($"{record.DisplayName}: {result.ErrorMessage}");
                }
            }

            if (publishedAssets.Count > 0)
            {
                try
                {
                    viewerIndexPublisher.Publish(
                        configuration.WebViewerDirectory,
                        configuration.ProjectName,
                        folderName,
                        publishedAssets,
                        configuration.ViewerPageSize);
                }
                catch (Exception exception)
                {
                    failures.Add($"展示索引未更新: {exception.Message}。已入库的素材不会丢失，可在修复网页数据后重新发布。 ");
                }
            }

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

        SummaryTextBlock.Text = $"共识别 {currentPreview.TotalCount} 条记录；可发布 {currentPreview.ReadyCount} 条；需处理 {currentPreview.NeedsAttentionCount} 条。";
    }

    private void UpdatePublishAvailability()
    {
        PublishButton.IsEnabled = currentPreview?.ReadyCount > 0;
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
        MainVideoName = Record.ScanResult.MainVideo?.Name ?? "未识别";
        PromptStatus = Record.ScanResult.Prompt switch
        {
            null when Record.ScanResult.CandidatePromptFiles.Count > 0 => "需确认候选文件",
            null => "需要手动填写",
            _ when string.IsNullOrWhiteSpace(Record.ScanResult.Prompt) => "需要手动填写",
            _ => "已读取 prompt.txt"
        };
        Status = Record.IsReadyForPublication ? "可发布" : "需要处理";
        Details = Record.ScanResult.ErrorMessage ?? Record.Metadata.Warning ?? (Record.IsReadyForPublication ? "校验通过" : "提示词不能为空");
        ReferenceCount = Record.ScanResult.References.Count.ToString();
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(null));
    }

    public bool IsMetadataMissing => Record.Metadata.Warning == "缺少必要的 metadata.csv 文件。";

    public string DisplayName { get; private set; } = string.Empty;

    public string MainVideoName { get; private set; } = string.Empty;

    public string ReferenceCount { get; private set; } = string.Empty;

    public string PromptStatus { get; private set; } = string.Empty;

    public string Status { get; private set; } = string.Empty;

    public string Details { get; private set; } = string.Empty;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
