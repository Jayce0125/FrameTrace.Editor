using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using FrameTrace.Editor.Domain;
using FrameTrace.Editor.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MessageBox = System.Windows.MessageBox;
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
    private LibraryAssetRow? editorAsset;
    private string? selectedCategoryPath;
    private readonly Dictionary<string, int> assetOrder = new(StringComparer.OrdinalIgnoreCase);
    private bool isLoadingEditor;
    private bool isRestoringSelection;
    private bool editorHasChanges;

    public LibraryManagerWindow()
    {
        InitializeComponent();
        AssetListView.ItemsSource = visibleAssets;
        CategoryTreeView.ItemsSource = categoryTree;
    }

    private static IEnumerable<CategoryNodeModel> OrderCategoryNodes(IReadOnlyList<CategoryNodeModel> nodes, ViewerOrderDocument order)
    {
        var byId = nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        return OrderChildren(null);

        IEnumerable<CategoryNodeModel> OrderChildren(string? parentId)
        {
            var ids = ViewerOrderStore.Siblings(order, parentId);
            var children = ids.Where(byId.ContainsKey).Select(id => byId[id])
                .Concat(nodes.Where(node => string.Equals(node.ParentId, parentId, StringComparison.OrdinalIgnoreCase) && !ids.Contains(node.Id, StringComparer.OrdinalIgnoreCase))
                    .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase));
            foreach (var child in children)
            {
                yield return child;
                foreach (var descendant in OrderChildren(child.Id)) yield return descendant;
            }
        }
    }

    public void Configure(LibraryConfiguration libraryConfiguration)
    {
        configuration = libraryConfiguration;
        ReloadData();
    }

    private void Reload_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (ConfirmDiscardEditorChanges("刷新列表"))
        {
            ReloadData();
        }
    }

    private void SortCurrentCategoryAssets_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (CategoryTreeView.SelectedItem is not CategoryTreeItem { Id: not null } category)
        {
            MessageBox.Show("请先在左侧选择一个分类。", "无法排序", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ExecuteOrderChange(
            () => categoryManager.SortRecordsByName(assets.Select(row => row.Asset).ToArray(), category.Id, configuration.WebViewerDirectory),
            $"“{category.Name}”下的素材已按名称重新排序。", true, false);
    }

    private void RepairAndPublish_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (!ConfirmDiscardEditorChanges("修复并发布索引"))
        {
            return;
        }

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
        SortCurrentCategoryAssetsButton.IsEnabled = false;
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
            categoryManager.SynchronizeRecordCategoryIds(records, configuration.WebViewerDirectory);
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
        var order = categoryManager.LoadOrder(configuration.WebViewerDirectory);
        LoadAssetOrder(order);
        if (nodes.Length > 0)
        {
            var items = nodes.ToDictionary(node => node.Id, node => new CategoryTreeItem(node.Id, node.Name, false, BuildCategoryPath(node, nodes), node.ParentId));
            foreach (var node in OrderCategoryNodes(nodes, order))
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

        var allAssets = new CategoryTreeItem(null, "全部分类", true, null);
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
                    item = new CategoryTreeItem(id, parts[index], false, path, parent?.Id);
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
            SortCurrentCategoryAssetsButton.IsEnabled = item.Id is not null;
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

        ExecuteOrderChange(
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

        ExecuteOrderChange(
            () => categoryManager.ReorderAssetAfter(assets.Select(row => row.Asset).ToArray(), source!.Asset, target!.Asset, configuration.WebViewerDirectory),
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
        foreach (var asset in assets.OrderBy(GetAssetOrder).ThenBy(asset => asset.DisplayName, StringComparer.OrdinalIgnoreCase))
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
            .OrderBy(GetAssetOrder).ThenBy(asset => asset.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            visibleAssets.Add(asset);
        }
    }

    private int GetAssetOrder(LibraryAssetRow asset) => assetOrder.TryGetValue(asset.Asset.Record.Id, out var order) ? order : int.MaxValue;

    private void ExecuteCategoryChange(Action action, string successMessage, bool showSuccessMessage = true, bool reloadCategories = true)
    {
        if (!ConfirmDiscardEditorChanges("调整分类"))
        {
            return;
        }

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

    private void ExecuteOrderChange(Action action, string successMessage, bool showSuccessMessage = true, bool reloadCategories = true)
    {
        if (!ConfirmDiscardEditorChanges("调整排序"))
        {
            return;
        }

        try
        {
            if (!LibraryPublishLock.TryAcquire(configuration.AssetsDirectory, out var publishLock, out var lockError)) { MessageBox.Show(lockError, "无法操作", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            using (publishLock) { action(); }

            LoadAssetOrder();
            if (reloadCategories)
            {
                LoadCategoryTree();
            }
            else if (selectedCategoryPath is null)
            {
                ShowAllAssets();
            }
            else if (FindCategoryTreeItemByPath(selectedCategoryPath) is { } selectedItem)
            {
                ShowAssetsFor(selectedItem);
            }
            else
            {
                ShowAllAssets();
            }

            if (showSuccessMessage)
            {
                MessageBox.Show(successMessage, "操作完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception exception) { MessageBox.Show($"排序操作失败：{exception.Message}", "操作失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void AssetListView_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (isRestoringSelection)
        {
            return;
        }

        var selectedRow = AssetListView.SelectedItems.Count == 1
            ? AssetListView.SelectedItem as LibraryAssetRow
            : null;
        if (!ReferenceEquals(editorAsset, selectedRow) && editorHasChanges)
        {
            var result = MessageBox.Show(
                "当前素材有未保存的修改。是否保存后切换？\n\n选择“否”将放弃修改；选择“取消”则继续编辑当前素材。",
                "未保存的修改",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);
            if (result == MessageBoxResult.Cancel)
            {
                RestoreEditorSelection();
                return;
            }

            if (result == MessageBoxResult.Yes && !SaveEditorChanges(false))
            {
                RestoreEditorSelection();
                return;
            }
        }

        if (selectedRow is null)
        {
            editorAsset = null;
            ClearEditor();
            EditorStatusTextBlock.Text = AssetListView.SelectedItems.Count > 1
                ? "请选择单条素材进行编辑；可继续多选以执行批量删除。"
                : "尚未选择素材。";
            return;
        }

        editorAsset = selectedRow;
        LoadEditor(selectedRow);
    }

    private void EditorField_Changed(object sender, RoutedEventArgs eventArgs)
    {
        if (isLoadingEditor || editorAsset is null)
        {
            return;
        }

        editorHasChanges = true;
        EditorStatusTextBlock.Text = "有未保存的修改。";
        SaveEditButton.IsEnabled = true;
        CancelEditButton.IsEnabled = true;
    }

    private void SaveEdit_Click(object sender, RoutedEventArgs eventArgs) => SaveEditorChanges(true);

    private void CancelEdit_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (editorAsset is not null)
        {
            LoadEditor(editorAsset);
        }
    }

    private bool SaveEditorChanges(bool showSuccessMessage)
    {
        if (editorAsset is null)
        {
            return false;
        }

        var displayName = DisplayNameTextBox.Text.Trim();
        var prompt = PromptTextBox.Text;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            MessageBox.Show("名称不能为空。", "无法保存", MessageBoxButton.OK, MessageBoxImage.Warning);
            DisplayNameTextBox.Focus();
            return false;
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            MessageBox.Show("提示词不能为空。", "无法保存", MessageBoxButton.OK, MessageBoxImage.Warning);
            PromptTextBox.Focus();
            return false;
        }

        DateTimeOffset? created = null;
        if (CreatedDatePicker.SelectedDate is DateTime selectedDate)
        {
            if (!DateTime.TryParseExact(CreatedTimeTextBox.Text.Trim(), "HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var selectedTime))
            {
                MessageBox.Show("创建时间格式无效，请使用 24 小时制 HH:mm，例如 14:30。", "无法保存", MessageBoxButton.OK, MessageBoxImage.Warning);
                CreatedTimeTextBox.Focus();
                return false;
            }

            created = new DateTimeOffset(selectedDate.Date.Add(selectedTime.TimeOfDay));
        }
        else if (!string.IsNullOrWhiteSpace(CreatedTimeTextBox.Text))
        {
            MessageBox.Show("请先选择创建日期，或清空时间。", "无法保存", MessageBoxButton.OK, MessageBoxImage.Warning);
            CreatedDatePicker.Focus();
            return false;
        }

        try
        {
            if (!LibraryPublishLock.TryAcquire(configuration.AssetsDirectory, out var publishLock, out var lockError))
            {
                MessageBox.Show(lockError, "无法保存", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            AssetRecord updatedRecord;
            using (publishLock)
            {
                updatedRecord = assetManager.UpdateTextAndMetadata(
                    editorAsset.Asset,
                    displayName,
                    prompt,
                    created,
                    NullIfWhiteSpace(QualityComboBox.SelectedItem as string ?? string.Empty),
                    NullIfWhiteSpace(AspectRatioComboBox.SelectedItem as string ?? string.Empty),
                    NullIfWhiteSpace(FeatureTextBox.Text),
                    NullIfWhiteSpace(SkillsTextBox.Text));
                indexPublisher.Publish(configuration.AssetsDirectory, configuration.WebViewerDirectory,
                    configuration.ProjectName, configuration.ViewerPageSize);
            }

            editorAsset.UpdateAsset(new LibraryAsset(editorAsset.Asset.RecordPath, updatedRecord));
            LoadEditor(editorAsset);
            if (showSuccessMessage)
            {
                MessageBox.Show("素材信息已保存，网页索引已更新。", "保存完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return true;
        }
        catch (Exception exception)
        {
            MessageBox.Show($"保存失败：{exception.Message}", "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void LoadEditor(LibraryAssetRow row)
    {
        isLoadingEditor = true;
        try
        {
            var record = row.Asset.Record;
            DisplayNameTextBox.Text = record.DisplayName;
            PromptTextBox.Text = record.Prompt;
            var created = record.Meta.Created?.LocalDateTime;
            CreatedDatePicker.SelectedDate = created?.Date;
            CreatedTimeTextBox.Text = created?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? string.Empty;
            SetComboOptions(QualityComboBox, configuration.QualityOptions.Select(option => option.Name), record.Meta.Quality);
            SetComboOptions(AspectRatioComboBox, configuration.AspectRatioOptions.Select(option => option.Name), record.Meta.AspectRatio);
            FeatureTextBox.Text = record.Meta.Feature ?? string.Empty;
            SkillsTextBox.Text = record.Meta.Skills ?? string.Empty;
        }
        finally
        {
            isLoadingEditor = false;
        }

        EditorEmptyStateTextBlock.Visibility = Visibility.Collapsed;
        EditorScrollViewer.Visibility = Visibility.Visible;
        EditorStatusTextBlock.Text = row.VideoName;
        editorHasChanges = false;
        SaveEditButton.IsEnabled = false;
        CancelEditButton.IsEnabled = true;
    }

    private static void SetComboOptions(System.Windows.Controls.ComboBox comboBox, IEnumerable<string> options, string? currentValue)
    {
        comboBox.Items.Clear();
        comboBox.Items.Add(string.Empty);
        foreach (var option in options)
        {
            comboBox.Items.Add(option);
        }

        if (!string.IsNullOrWhiteSpace(currentValue) &&
            !comboBox.Items.Cast<string>().Contains(currentValue, StringComparer.OrdinalIgnoreCase))
        {
            comboBox.Items.Add(currentValue);
        }

        comboBox.SelectedItem = currentValue ?? string.Empty;
    }

    private bool ConfirmDiscardEditorChanges(string operation)
    {
        if (!editorHasChanges)
        {
            return true;
        }

        if (MessageBox.Show(
                $"当前素材有未保存的修改。继续{operation}将放弃这些修改，是否继续？",
                "未保存的修改",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return false;
        }

        if (editorAsset is not null)
        {
            LoadEditor(editorAsset);
        }

        return true;
    }

    private void ClearEditor()
    {
        isLoadingEditor = true;
        DisplayNameTextBox.Clear();
        PromptTextBox.Clear();
        CreatedDatePicker.SelectedDate = null;
        CreatedTimeTextBox.Clear();
        QualityComboBox.SelectedIndex = -1;
        AspectRatioComboBox.SelectedIndex = -1;
        FeatureTextBox.Clear();
        SkillsTextBox.Clear();
        isLoadingEditor = false;
        EditorScrollViewer.Visibility = Visibility.Collapsed;
        EditorEmptyStateTextBlock.Visibility = Visibility.Visible;
        editorHasChanges = false;
        SaveEditButton.IsEnabled = false;
        CancelEditButton.IsEnabled = false;
    }

    private void RestoreEditorSelection()
    {
        isRestoringSelection = true;
        try
        {
            AssetListView.SelectedItems.Clear();
            if (editorAsset is not null)
            {
                AssetListView.SelectedItems.Add(editorAsset);
            }
        }
        finally
        {
            isRestoringSelection = false;
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
        categoryManager.SynchronizeRecordCategoryIds(records, configuration.WebViewerDirectory);
        LoadAssetOrder();

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

    private void LoadAssetOrder()
    {
        LoadAssetOrder(categoryManager.LoadOrder(configuration.WebViewerDirectory));
    }

    private void LoadAssetOrder(ViewerOrderDocument order)
    {
        assetOrder.Clear();
        foreach (var ids in order.CategoryAssets.Values)
            foreach (var id in ids)
                if (!assetOrder.ContainsKey(id)) assetOrder[id] = assetOrder.Count;
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
        if (!ConfirmDiscardEditorChanges("删除素材"))
        {
            return;
        }

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
        if (!ConfirmDiscardEditorChanges("清空素材库"))
        {
            return;
        }

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

}

public sealed class LibraryAssetRow : System.ComponentModel.INotifyPropertyChanged
{
    private LibraryAsset asset;
    private bool isDropTarget;

    public LibraryAssetRow(LibraryAsset asset)
    {
        this.asset = asset;
    }

    public LibraryAsset Asset => asset;
    public string DisplayName => Asset.Record.DisplayName;
    public string FolderName => Asset.Record.FolderName;
    public string VideoName => Asset.Record.LocalPath;

    public void UpdateAsset(LibraryAsset updatedAsset)
    {
        asset = updatedAsset;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayName)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(FolderName)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(VideoName)));
    }

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
    public CategoryTreeItem(string? id, string name, bool isExpanded, string? path, string? parentId = null)
    {
        Id = id;
        Name = name;
        IsExpanded = isExpanded;
        Path = path;
        ParentId = parentId;
    }

    public string? Id { get; }
    public string Name { get; }
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