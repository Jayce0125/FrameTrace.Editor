using System.Collections.ObjectModel;
using System.IO;
using FrameTrace.Editor.Services;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;

namespace FrameTrace.Editor;

public partial class LibraryManagerWindow : Window
{
    private readonly LibraryConfiguration configuration;
    private readonly LibraryAssetManager assetManager = new();
    private readonly ViewerIndexPublisher indexPublisher = new();
    private readonly ObservableCollection<LibraryAssetRow> assets = [];

    public LibraryManagerWindow(LibraryConfiguration configuration)
    {
        InitializeComponent();
        this.configuration = configuration;
        AssetListView.ItemsSource = assets;
        Reload();
    }

    private void Reload_Click(object sender, RoutedEventArgs eventArgs) => Reload();

    private void Reload()
    {
        var records = assetManager.Load(configuration.AssetsDirectory, out var warnings);
        assets.Clear();
        foreach (var record in records)
        {
            assets.Add(new LibraryAssetRow(record));
        }

        SummaryTextBlock.Text = $"已入库 {assets.Count} 条素材。" + (warnings.Count == 0 ? string.Empty : $" 已跳过 {warnings.Count} 条损坏记录。");
    }

    private void AssetListView_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (AssetListView.SelectedItem is not LibraryAssetRow row)
        {
            return;
        }

        DisplayNameTextBox.Text = row.Asset.Record.DisplayName;
        FolderNameTextBox.Text = row.Asset.Record.FolderName;
        PromptTextBox.Text = row.Asset.Record.Prompt;
        AuthorTextBox.Text = row.Asset.Record.Author ?? string.Empty;
        TagsTextBox.Text = string.Join(", ", row.Asset.Record.Tags);
        RatingTextBox.Text = row.Asset.Record.Rating?.ToString() ?? string.Empty;
        ReviewStatusComboBox.Text = row.Asset.Record.ReviewStatus ?? "未审核";
        VideoPathTextBox.Clear();
        PosterPathTextBox.Clear();
        ReferencePathsTextBox.Clear();
    }

    private void Save_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (AssetListView.SelectedItem is not LibraryAssetRow row)
        {
            MessageBox.Show("请先选择一条素材。", "无法保存", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            if (!LibraryPublishLock.TryAcquire(configuration.AssetsDirectory, out var publishLock, out var lockError))
            {
                MessageBox.Show(lockError, "无法保存", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            using (publishLock)
            {
                var updated = assetManager.Save(row.Asset, new AssetEdit(
                    DisplayNameTextBox.Text, FolderNameTextBox.Text, PromptTextBox.Text, AuthorTextBox.Text,
                    TagsTextBox.Text, RatingTextBox.Text, ReviewStatusComboBox.Text, VideoPathTextBox.Text,
                    PosterPathTextBox.Text, SplitReferencePaths()), configuration);
                indexPublisher.Update(configuration.WebViewerDirectory, configuration.ProjectName, row.Asset.Record.FolderName,
                    updated, Path.GetDirectoryName(row.Asset.RecordPath)!, configuration.ViewerPageSize);
            }
            Reload();
            MessageBox.Show("修改已保存，网页索引已更新。", "保存完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show($"保存失败：{exception.Message}", "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (AssetListView.SelectedItem is not LibraryAssetRow row)
        {
            MessageBox.Show("请先选择一条素材。", "无法删除", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (MessageBox.Show($"确定删除“{row.Asset.Record.DisplayName}”及其视频、引用资源和提示词吗？此操作无法撤销。", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (!LibraryPublishLock.TryAcquire(configuration.AssetsDirectory, out var publishLock, out var lockError))
            {
                MessageBox.Show(lockError, "无法删除", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            using (publishLock)
            {
                indexPublisher.Remove(configuration.WebViewerDirectory, configuration.ProjectName, row.Asset.Record, configuration.ViewerPageSize);
                assetManager.Delete(row.Asset);
            }
            Reload();
            DisplayNameTextBox.Clear();
            FolderNameTextBox.Clear();
            PromptTextBox.Clear();
            MessageBox.Show("素材已删除，网页索引已更新。", "删除完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show($"删除失败：{exception.Message}", "删除失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BrowseVideo_Click(object sender, RoutedEventArgs eventArgs) => VideoPathTextBox.Text = SelectFile("视频文件|*.mp4;*.mov;*.mkv;*.avi;*.webm");

    private void BrowsePoster_Click(object sender, RoutedEventArgs eventArgs) => PosterPathTextBox.Text = SelectFile("图片文件|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp");

    private void BrowseReferences_Click(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new OpenFileDialog { Filter = "图片文件|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp", Multiselect = true };
        if (dialog.ShowDialog() == true)
        {
            ReferencePathsTextBox.Text = string.Join("|", dialog.FileNames);
        }
    }

    private static string SelectFile(string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter };
        return dialog.ShowDialog() == true ? dialog.FileName : string.Empty;
    }

    private IReadOnlyList<string> SplitReferencePaths() => ReferencePathsTextBox.Text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public sealed class LibraryAssetRow(LibraryAsset asset)
{
    public LibraryAsset Asset { get; } = asset;
    public string DisplayName => Asset.Record.DisplayName;
    public string FolderName => Asset.Record.FolderName;
    public string VideoName => Asset.Record.LocalPath;
}