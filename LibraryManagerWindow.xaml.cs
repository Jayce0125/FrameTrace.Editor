using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using FrameTrace.Editor.Services;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WpfDragDropEffects = System.Windows.DragDropEffects;
using WpfDataObject = System.Windows.DataObject;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPoint = System.Windows.Point;
using System.Diagnostics;

namespace FrameTrace.Editor;

public partial class LibraryManagerWindow : System.Windows.Controls.UserControl
{
    private LibraryConfiguration configuration = null!;
    private readonly LibraryAssetManager assetManager = new();
    private readonly TreeViewerIndexPublisher indexPublisher = new();
    private readonly CategoryManagementService categoryManager = new();
    private readonly ObservableCollection<LibraryAssetRow> assets = [];
    private readonly ObservableCollection<LibraryAssetRow> visibleAssets = [];
    private readonly ObservableCollection<CategoryTreeItem> categoryTree = [];
    private WpfPoint categoryDragStartPoint;
    private CategoryTreeItem? draggedCategory;
    private CategoryTreeItem? dropPreviewCategory;
    private WpfPoint assetDragStartPoint;
    private LibraryAssetRow? draggedAsset;
    private LibraryAssetRow? assetDropPreviewRow;
    private string? selectedCategoryPath;

    public LibraryManagerWindow()
    {
        InitializeComponent();
        AssetListView.ItemsSource = visibleAssets;
        CategoryTreeView.ItemsSource = categoryTree;
    }

    public void Configure(LibraryConfiguration libraryConfiguration)
    {
        configuration = libraryConfiguration;
        ReloadData();
    }

    private void Reload_Click(object sender, RoutedEventArgs eventArgs) => ReloadData();

    private void RepairAndPublish_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (!configuration.IsConfigured)
        {
            MessageBox.Show("请先在 config.json 中配置素材库目录。", "无法发布", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            if (!LibraryPublishLock.TryAcquire(configuration.AssetsDirectory, out var publishLock, out var lockError))
            {
                MessageBox.Show(lockError, "无法发布", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            using (publishLock)
            {
                indexPublisher.Publish(configuration.AssetsDirectory, configuration.WebViewerDirectory,
                    configuration.ProjectName, configuration.ViewerPageSize);
            }

            ReloadData();
            MessageBox.Show("路径已修复，网页索引已重新发布。", "发布完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show($"修复并发布失败：{exception.Message}", "发布失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void ReloadData()
    {
        if (!configuration.IsConfigured)
        {
            SummaryTextBlock.Text = "请先在 config.json 中配置素材库目录。";
            return;
        }

        try
        {
            var records = assetManager.Load(configuration.AssetsDirectory, out var warnings);
            assets.Clear();
            foreach (var record in records)
            {
                assets.Add(new LibraryAssetRow(record));
            }
            LoadCategoryTree();

            ShowAllAssets();
            SummaryTextBlock.Text = $"已入库 {assets.Count} 条素材。" + (warnings.Count == 0 ? string.Empty : $" 已跳过 {warnings.Count} 条损坏记录。");
        }
        catch (Exception exception)
        {
            assets.Clear();
            visibleAssets.Clear();
            categoryTree.Clear();
            SummaryTextBlock.Text = $"读取管理数据失败：{exception.Message}";
            MessageBox.Show($"无法打开素材管理数据：{exception.Message}\n\n请先点击“刷新列表”重试。", "读取失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LoadCategoryTree()
    {
        categoryTree.Clear();
        var nodes = categoryManager.Load(configuration.WebViewerDirectory)
            .Where(node => !string.IsNullOrWhiteSpace(node.Id) && !string.IsNullOrWhiteSpace(node.Name))
            .ToArray();
        if (nodes.Length > 0)
        {
            var items = nodes.ToDictionary(node => node.Id, node => new CategoryTreeItem(node.Id, node.Name, node.Order, false, BuildCategoryPath(node, nodes), node.ParentId));
            foreach (var node in nodes)
            {
                if (node.ParentId is not null && items.TryGetValue(node.ParentId, out var parent))
                {
                    parent.Children.Add(items[node.Id]);
                }
                else
                {
                    categoryTree.Add(items[node.Id]);
                }
            }
        }
        else
        {
            BuildCategoryTreeFromAssets();
        }

        foreach (var item in categoryTree)
        {
            item.SortChildren();
        }

        var allAssets = new CategoryTreeItem(null, "全部分类", -1, true, null);
        foreach (var item in categoryTree)
        {
            allAssets.Children.Add(item);
        }
        categoryTree.Clear();
        categoryTree.Add(allAssets);
    }

    private static string BuildCategoryPath(CategoryNodeModel node, IReadOnlyList<CategoryNodeModel> nodes)
    {
        var map = nodes.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var names = new List<string> { node.Name };
        var parentId = node.ParentId;
        while (parentId is not null && map.TryGetValue(parentId, out var parent))
        {
            names.Insert(0, parent.Name);
            parentId = parent.ParentId;
        }

        return string.Join("/", names);
    }

    private void BuildCategoryTreeFromAssets()
    {
        var items = new Dictionary<string, CategoryTreeItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets)
        {
            var parent = (CategoryTreeItem?)null;
            var path = string.Empty;
            var parts = (asset.FolderName ?? string.Empty).Split('/', '\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var index = 0; index < parts.Length; index++)
            {
                path = path.Length == 0 ? parts[index] : $"{path}/{parts[index]}";
                if (!items.TryGetValue(path, out var item))
                {
                    var id = index == parts.Length - 1 && !string.IsNullOrWhiteSpace(asset.Asset.Record.CategoryId)
                        ? asset.Asset.Record.CategoryId
                        : path;
                    item = new CategoryTreeItem(id, parts[index], index, false, path, parent?.Id);
                    items.Add(path, item);
                    if (parent is null)
                    {
                        categoryTree.Add(item);
                    }
                    else
                    {
                        parent.Children.Add(item);
                    }
                }

                parent = item;
            }
        }
    }

    private void CategoryTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> eventArgs)
    {
        if (eventArgs.NewValue is CategoryTreeItem item)
        {
            selectedCategoryPath = item.Path;
            ShowAssetsFor(item);
        }
    }

    private void CategoryTreeView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        categoryDragStartPoint = eventArgs.GetPosition(CategoryTreeView);
        draggedCategory = FindCategoryTreeItem(eventArgs.OriginalSource as DependencyObject);
    }

    private void CategoryTreeView_PreviewMouseMove(object sender, WpfMouseEventArgs eventArgs)
    {
        if (eventArgs.LeftButton != MouseButtonState.Pressed || draggedCategory?.Id is null)
        {
            return;
        }

        var currentPoint = eventArgs.GetPosition(CategoryTreeView);
        if (Math.Abs(currentPoint.X - categoryDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPoint.Y - categoryDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var category = draggedCategory;
        draggedCategory = null;
        DragDrop.DoDragDrop(CategoryTreeView, new WpfDataObject(typeof(CategoryTreeItem), category), WpfDragDropEffects.Move);
    }

    private void CategoryTreeView_DragOver(object sender, WpfDragEventArgs eventArgs)
    {
        var source = eventArgs.Data.GetData(typeof(CategoryTreeItem)) as CategoryTreeItem;
        var target = FindCategoryTreeItem(eventArgs.OriginalSource as DependencyObject);
        ClearDropPreview();
        if (target is not null && target != source)
        {
            target.IsDropTarget = CanReorder(source, target);
            dropPreviewCategory = target;
        }

        eventArgs.Effects = CanReorder(source, target) ? WpfDragDropEffects.Move : WpfDragDropEffects.None;
        eventArgs.Handled = true;
    }

    private void CategoryTreeView_DragLeave(object sender, WpfDragEventArgs eventArgs) => ClearDropPreview();

    private void CategoryTreeView_Drop(object sender, WpfDragEventArgs eventArgs)
    {
        var source = eventArgs.Data.GetData(typeof(CategoryTreeItem)) as CategoryTreeItem;
        var target = FindCategoryTreeItem(eventArgs.OriginalSource as DependencyObject);
        if (!CanReorder(source, target))
        {
            ClearDropPreview();
            return;
        }

        ExecuteCategoryChange(
            () => categoryManager.ReorderAfter(configuration.WebViewerDirectory, source!.Id!, target!.Id!),
            "分类顺序已更新。", false);
        ClearDropPreview();
        eventArgs.Handled = true;
    }

    private void ClearDropPreview()
    {
        if (dropPreviewCategory is null)
        {
            return;
        }

        dropPreviewCategory.IsDropTarget = false;
        dropPreviewCategory = null;
    }

    private void AssetListView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        assetDragStartPoint = eventArgs.GetPosition(AssetListView);
        draggedAsset = FindAssetRow(eventArgs.OriginalSource as DependencyObject);
    }

    private void AssetListView_PreviewMouseMove(object sender, WpfMouseEventArgs eventArgs)
    {
        if (eventArgs.LeftButton != MouseButtonState.Pressed || draggedAsset is null)
        {
            return;
        }

        var currentPoint = eventArgs.GetPosition(AssetListView);
        if (Math.Abs(currentPoint.X - assetDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPoint.Y - assetDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var asset = draggedAsset;
        draggedAsset = null;
        DragDrop.DoDragDrop(AssetListView, new WpfDataObject(typeof(LibraryAssetRow), asset), WpfDragDropEffects.Move);
    }

    private void AssetListView_DragOver(object sender, WpfDragEventArgs eventArgs)
    {
        var source = eventArgs.Data.GetData(typeof(LibraryAssetRow)) as LibraryAssetRow;
        var target = FindAssetRow(eventArgs.OriginalSource as DependencyObject);

        if (!ReferenceEquals(assetDropPreviewRow, target))
        {
            ClearAssetDropPreview();
        }

        if (target is not null && target != source)
        {
            target.IsDropTarget = CanReorder(source, target);
            assetDropPreviewRow = target;
        }

        eventArgs.Effects = CanReorder(source, target) ? WpfDragDropEffects.Move : WpfDragDropEffects.None;
        eventArgs.Handled = true;
    }

    private void AssetListView_DragLeave(object sender, WpfDragEventArgs eventArgs) => ClearAssetDropPreview();

    private void AssetListView_Drop(object sender, WpfDragEventArgs eventArgs)
    {
        var source = eventArgs.Data.GetData(typeof(LibraryAssetRow)) as LibraryAssetRow;
        var target = FindAssetRow(eventArgs.OriginalSource as DependencyObject);
        if (!CanReorder(source, target))
        {
            ClearAssetDropPreview();
            return;
        }

        ExecuteCategoryChange(
            () => categoryManager.ReorderAssetAfter(assets.Select(row => row.Asset).ToArray(), source!.Asset, target!.Asset),
            "素材顺序已更新。", false, false);
        ClearAssetDropPreview();
        eventArgs.Handled = true;
    }

    private void ClearAssetDropPreview()
    {
        if (assetDropPreviewRow is null)
        {
            return;
        }

        assetDropPreviewRow.IsDropTarget = false;
        assetDropPreviewRow = null;
    }

    private static bool CanReorder(LibraryAssetRow? source, LibraryAssetRow? target) =>
        source is not null && target is not null &&
        !ReferenceEquals(source, target) &&
        string.Equals(source.Asset.Record.FolderName, target.Asset.Record.FolderName, StringComparison.OrdinalIgnoreCase);

    private static LibraryAssetRow? FindAssetRow(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement element && element.DataContext is LibraryAssetRow row)
            {
                return row;
            }

            source = source is Visual visual ? VisualTreeHelper.GetParent(visual) : null;
        }

        return null;
    }

    private static bool CanReorder(CategoryTreeItem? source, CategoryTreeItem? target) =>
        source?.Id is not null && target?.Id is not null &&
        !string.Equals(source.Id, target.Id, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(source.ParentId, target.ParentId, StringComparison.OrdinalIgnoreCase);

    private static CategoryTreeItem? FindCategoryTreeItem(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement element && element.DataContext is CategoryTreeItem item)
            {
                return item;
            }

            source = source is Visual visual ? VisualTreeHelper.GetParent(visual) : null;
        }

        return null;
    }

    private void ShowAllAssets()
    {
        visibleAssets.Clear();
        foreach (var asset in assets.OrderBy(asset => asset.Asset.Record.Order))
        {
            visibleAssets.Add(asset);
        }
    }

    private void ShowAssetsFor(CategoryTreeItem item)
    {
        visibleAssets.Clear();
        foreach (var asset in assets.Where(asset => item.Path is null ||
            string.Equals(asset.FolderName, item.Path, StringComparison.OrdinalIgnoreCase) ||
            asset.FolderName.StartsWith(item.Path + "/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(asset => asset.Asset.Record.Order))
        {
            visibleAssets.Add(asset);
        }
    }

    private void ExecuteCategoryChange(Action action, string successMessage, bool showSuccessMessage = true, bool reloadCategories = true)
    {
        try
        {
            if (!LibraryPublishLock.TryAcquire(configuration.AssetsDirectory, out var publishLock, out var lockError)) { MessageBox.Show(lockError, "无法操作", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            using (publishLock) { action(); indexPublisher.Publish(configuration.AssetsDirectory, configuration.WebViewerDirectory, configuration.ProjectName, configuration.ViewerPageSize); }
            if (reloadCategories)
            {
                ReloadData();
            }
            else
            {
                ReloadAssetDataOnly();
            }
            if (showSuccessMessage)
            {
                MessageBox.Show(successMessage, "操作完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception exception) { MessageBox.Show($"分类操作失败：{exception.Message}", "操作失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void AssetListView_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (AssetListView.SelectedItem is not LibraryAssetRow row)
        {
            return;
        }

        VideoPathTextBox.Clear();
        PosterPathTextBox.Clear();
        ReferencePathsTextBox.Clear();
    }

    private void ReplaceMedia_Click(object sender, RoutedEventArgs eventArgs)
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
                var updated = assetManager.ReplaceMedia(row.Asset, VideoPathTextBox.Text, PosterPathTextBox.Text,
                    SplitReferencePaths(), configuration);
                indexPublisher.Publish(configuration.AssetsDirectory, configuration.WebViewerDirectory,
                    configuration.ProjectName, configuration.ViewerPageSize);
            }
            ReloadData();
            MessageBox.Show("素材已重新上传，网页索引已更新。", "上传完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show($"保存失败：{exception.Message}", "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ReloadAssetDataOnly()
    {
        var records = assetManager.Load(configuration.AssetsDirectory, out var warnings);
        assets.Clear();
        foreach (var record in records)
        {
            assets.Add(new LibraryAssetRow(record));
        }

        if (selectedCategoryPath is null)
        {
            ShowAllAssets();
            return;
        }

        var selectedItem = FindCategoryTreeItemByPath(selectedCategoryPath);
        if (selectedItem is null)
        {
            ShowAllAssets();
            return;
        }

        ShowAssetsFor(selectedItem);
    }

    private CategoryTreeItem? FindCategoryTreeItemByPath(string path)
    {
        return categoryTree.SelectMany(FlattenCategoryTree)
            .FirstOrDefault(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<CategoryTreeItem> FlattenCategoryTree(CategoryTreeItem item)
    {
        yield return item;
        foreach (var child in item.Children.SelectMany(FlattenCategoryTree))
        {
            yield return child;
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (AssetListView.SelectedItem is not LibraryAssetRow row)
        {
            MessageBox.Show("请先选择一条素材。", "无法删除", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DeleteAssets([row], "删除素材", $"确定删除“{row.Asset.Record.DisplayName}”及其视频、引用资源和提示词吗？此操作无法撤销。", "素材已删除，网页索引已更新。", "删除完成");
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs eventArgs)
    {
        var selected = AssetListView.SelectedItems.Cast<LibraryAssetRow>().ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show("请先选择要删除的素材。", "无法删除", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DeleteAssets(selected, "批量删除", $"确定删除选中的 {selected.Length} 条素材及其视频、引用资源和提示词吗？此操作无法撤销。", $"已删除 {selected.Length} 条素材，网页索引已更新。", "删除完成");
    }

    private void ClearLibrary_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (assets.Count == 0)
        {
            MessageBox.Show("素材库已经为空。", "无需清空", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show($"确定清空素材库中的全部 {assets.Count} 条素材吗？这会删除所有视频、封面、引用资源和提示词，且无法撤销。", "确认清空素材库", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (!LibraryPublishLock.TryAcquire(configuration.AssetsDirectory, out var publishLock, out var lockError))
            {
                MessageBox.Show(lockError, "无法清空", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            using (publishLock)
            {
                assetManager.Clear(configuration.AssetsDirectory);
                indexPublisher.Publish(configuration.AssetsDirectory, configuration.WebViewerDirectory,
                    configuration.ProjectName, configuration.ViewerPageSize);
            }
            ReloadData();
            MessageBox.Show("素材库已清空，网页索引已更新。", "清空完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show($"清空失败：{exception.Message}", "清空失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DeleteAssets(IReadOnlyList<LibraryAssetRow> rows, string title, string confirmation, string successMessage, string successTitle)
    {
        if (MessageBox.Show(confirmation, $"确认{title}", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (!LibraryPublishLock.TryAcquire(configuration.AssetsDirectory, out var publishLock, out var lockError))
            {
                MessageBox.Show(lockError, $"无法{title}", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            using (publishLock)
            {
                foreach (var row in rows)
                {
                    assetManager.Delete(row.Asset);
                }

                indexPublisher.Publish(configuration.AssetsDirectory, configuration.WebViewerDirectory,
                    configuration.ProjectName, configuration.ViewerPageSize);
            }
            ReloadData();
            MessageBox.Show(successMessage, successTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show($"{title}失败：{exception.Message}", $"{title}失败", MessageBoxButton.OK, MessageBoxImage.Warning);
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

public sealed class LibraryAssetRow
{
    public LibraryAssetRow(LibraryAsset asset)
    {
        Asset = asset;
    }

    public LibraryAsset Asset { get; }
    public string DisplayName => Asset.Record.DisplayName;
    public string FolderName => Asset.Record.FolderName;
    public string VideoName => Asset.Record.LocalPath;

    private bool isDropTarget;
    public bool IsDropTarget
    {
        get => isDropTarget;
        set
        {
            if (isDropTarget == value) return;
            isDropTarget = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsDropTarget)));

            Debug.WriteLine($"{DisplayName}.IsDropTarget:{value}" );
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed class CategoryOption
{
    public CategoryOption(CategoryNodeModel node, string path) { Node = node; Path = path; }
    public CategoryNodeModel Node { get; }
    public string Path { get; }
}

public sealed class CategoryTreeItem : System.ComponentModel.INotifyPropertyChanged
{
    public CategoryTreeItem(string? id, string name, int order, bool isExpanded, string? path, string? parentId = null)
    {
        Id = id;
        Name = name;
        Order = order;
        IsExpanded = isExpanded;
        Path = path;
        ParentId = parentId;
    }

    public string? Id { get; }
    public string Name { get; }
    public int Order { get; }
    public bool IsExpanded { get; }
    public string? Path { get; }
    public string? ParentId { get; }
    private bool isDropTarget;
    public bool IsDropTarget
    {
        get => isDropTarget;
        set
        {
            if (isDropTarget == value) return;
            isDropTarget = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsDropTarget)));


        }
    }

    public ObservableCollection<CategoryTreeItem> Children { get; } = [];

    public HashSet<string> GetDescendantIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddDescendantIds(this, ids);
        return ids;
    }

    public void SortChildren()
    {
        var sorted = Children.OrderBy(child => child.Order).ThenBy(child => child.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        Children.Clear();
        foreach (var child in sorted)
        {
            child.SortChildren();
            Children.Add(child);
        }
    }

    private static void AddDescendantIds(CategoryTreeItem item, HashSet<string> ids)
    {
        if (item.Id is not null)
        {
            ids.Add(item.Id);
        }

        foreach (var child in item.Children)
        {
            AddDescendantIds(child, ids);
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}