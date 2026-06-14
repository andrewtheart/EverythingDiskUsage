using EverythingDiskUsage.Models;
using EverythingDiskUsage.Native;
using EverythingDiskUsage.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Forms = System.Windows.Forms;
using IoPath = System.IO.Path;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using PathShape = System.Windows.Shapes.Path;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfPoint = System.Windows.Point;

namespace EverythingDiskUsage;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private const int MaxVisibleFileRows = 1000;

    private static readonly MediaColor[] SliceColors =
    [
        MediaColor.FromRgb(21, 122, 140),
        MediaColor.FromRgb(235, 159, 54),
        MediaColor.FromRgb(83, 139, 82),
        MediaColor.FromRgb(190, 80, 72),
        MediaColor.FromRgb(82, 116, 174),
        MediaColor.FromRgb(137, 104, 168),
        MediaColor.FromRgb(78, 163, 151),
        MediaColor.FromRgb(205, 119, 71),
        MediaColor.FromRgb(99, 121, 133),
        MediaColor.FromRgb(48, 64, 84)
    ];

    private readonly DiskUsageAnalyzer _analyzer = new();
    private readonly ObservableCollection<DirectoryUsageNode> _treeRoots = [];
    private readonly ObservableCollection<DirectoryUsageNode> _directoryDetails = [];
    private readonly ObservableCollection<FileDetailRow> _fileDetails = [];
    private readonly ObservableCollection<DuplicateFileRow> _duplicateRows = [];
    private readonly Forms.NotifyIcon _notifyIcon = new();
    private CancellationTokenSource? _scanCancellation;
    private DirectoryUsageNode? _selectedNode;
    private DirectoryUsageNode? _currentRoot;
    private string? _currentRootPath;
    private IReadOnlyList<FileUsageItem> _allFilesSorted = [];
    private bool _isUpdatingScanView;
    private DateTime _lastUiProgressLogUtc = DateTime.MinValue;
    private long _lastUiProgressLoggedFiles;

    private sealed record DuplicateRowsSnapshot(
        IReadOnlyList<DuplicateFileRow> Rows,
        int SourceFileCount,
        int TotalGroups,
        long TotalWastedBytes,
        int MaxGroups);

    private sealed record ScanViewSnapshot(
        DirectoryUsageNode Root,
        IReadOnlyList<FileUsageItem> Files,
        DuplicateRowsSnapshot Duplicates);

    private enum ShellItemKind
    {
        File,
        Directory
    }

    public MainWindow()
    {
        AppLogger.Info("MainWindow constructor starting");
        InitializeComponent();
        UsageTree.ItemsSource = _treeRoots;
        DirectoryDetailsGrid.ItemsSource = _directoryDetails;
        FileDetailsGrid.ItemsSource = _fileDetails;
        DuplicatesGrid.ItemsSource = _duplicateRows;
        ConfigureNotifications();
        AppLogger.Info("MainWindow constructor completed; item sources assigned");
    }

    protected override void OnClosed(EventArgs e)
    {
        AppLogger.Info("MainWindow closing; disposing notification icon");
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        base.OnClosed(e);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        using var operation = AppLogger.TimedOperation("Window_Loaded");

        var readyDrives = DriveInfo.GetDrives()
            .Where(drive => drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable)
            .OrderBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var drive in readyDrives)
        {
            try
            {
                AppLogger.Info($"Drive ready: name='{drive.Name}', type={drive.DriveType}, format='{drive.DriveFormat}', totalBytes={drive.TotalSize}, availableBytes={drive.AvailableFreeSpace}, volumeLabel='{drive.VolumeLabel}'");
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"Drive ready but metadata logging failed; name='{drive.Name}', type={drive.DriveType}");
                AppLogger.Error("Drive metadata logging failure", ex);
            }
        }

        var drives = readyDrives
            .Select(drive => drive.Name)
            .ToList();

        DriveComboBox.ItemsSource = drives;
        var defaultDrive = drives.FirstOrDefault(name => name.Equals(@"C:\", StringComparison.OrdinalIgnoreCase))
            ?? drives.FirstOrDefault()
            ?? @"C:\";

        AppLogger.Info($"Drive selector initialized; count={drives.Count}; defaultDrive='{defaultDrive}'");

        DriveComboBox.SelectedItem = defaultDrive;
        RootPathTextBox.Text = defaultDrive;
        SdkStatusTextBlock.Text = "Ready for SDK scan";
        UpdateDriveSummary(defaultDrive, null);
    }

    private void DriveComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DriveComboBox.SelectedItem is string driveName)
        {
            AppLogger.Info($"Drive selection changed; drive='{driveName}'");
            RootPathTextBox.Text = driveName;
            UpdateDriveSummary(driveName, null);
        }
    }

    private void RootPathTextBox_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AppLogger.Info($"Root path textbox Enter pressed; path='{RootPathTextBox.Text}'");
            e.Handled = true;
            _ = StartScanAsync();
        }
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var initialDirectory = Directory.Exists(RootPathTextBox.Text) ? RootPathTextBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        AppLogger.Info($"Browse requested; currentPath='{RootPathTextBox.Text}', initialDirectory='{initialDirectory}'");

        using var dialog = new Forms.FolderBrowserDialog
        {
            InitialDirectory = initialDirectory,
            Description = "Choose a folder to scan",
            UseDescriptionForTitle = true
        };

        var dialogResult = dialog.ShowDialog();
        AppLogger.Info($"Browse dialog closed; result={dialogResult}; selectedPath='{dialog.SelectedPath}'");

        if (dialogResult == Forms.DialogResult.OK)
        {
            RootPathTextBox.Text = EnsureTrailingSeparator(dialog.SelectedPath);
            UpdateDriveSummary(dialog.SelectedPath, null);
        }
    }

    private void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.Info($"Scan button clicked; path='{RootPathTextBox.Text}'");
        _ = StartScanAsync();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.Warning($"Cancel button clicked; activeScan={_scanCancellation is not null}");
        _scanCancellation?.Cancel();
    }

    private async Task StartScanAsync()
    {
        if (_scanCancellation is not null)
        {
            AppLogger.Warning("StartScanAsync ignored because a scan is already running");
            return;
        }

        var rootPath = RootPathTextBox.Text.Trim();
        AppLogger.Info($"StartScanAsync requested; rawPath='{RootPathTextBox.Text}', trimmedPath='{rootPath}'");
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            AppLogger.Warning($"StartScanAsync rejected path; exists={Directory.Exists(rootPath)}; path='{rootPath}'");
            System.Windows.MessageBox.Show(this, "Choose an existing folder or drive root.", "Path not found", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _scanCancellation = new CancellationTokenSource();
        AppLogger.Info($"Scan cancellation source created; tokenHash={_scanCancellation.Token.GetHashCode()}");
        SetScanning(true);
        _treeRoots.Clear();
        _directoryDetails.Clear();
        _fileDetails.Clear();
        _allFilesSorted = [];
        _selectedNode = null;
        _currentRoot = null;
        _currentRootPath = null;
        _isUpdatingScanView = false;
        _lastUiProgressLogUtc = DateTime.MinValue;
        _lastUiProgressLoggedFiles = 0;
        LegendItems.ItemsSource = null;
        PieCanvas.Children.Clear();
        EmptyPieTextBlock.Visibility = Visibility.Visible;
        FolderDetailsSummaryTextBlock.Text = string.Empty;
        FileDetailsSummaryTextBlock.Text = string.Empty;
        _duplicateRows.Clear();
        DuplicatesSummaryTextBlock.Text = string.Empty;
        AppLogger.Debug("Cleared previous scan UI state");

        var progress = new Progress<ScanProgress>(UpdateProgress);

        try
        {
            AppLogger.Info($"Calling DiskUsageAnalyzer.ScanAsync; rootPath='{rootPath}'");
            var result = await _analyzer.ScanAsync(rootPath, progress, _scanCancellation.Token);
            AppLogger.Info($"ScanAsync completed; totalResults={result.TotalResults}; files={result.Root.FileCount}; bytes={result.Root.SizeBytes}; elapsedMs={result.Elapsed.TotalMilliseconds:0}");
            _currentRoot = result.Root;
            _currentRootPath = result.Root.FullPath;
            _treeRoots.Add(result.Root);

            using (AppLogger.TimedOperation($"Sort file details; fileCount={result.Files.Count}"))
            {
                _allFilesSorted = result.Files
                    .OrderByDescending(file => file.SizeBytes)
                    .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            _selectedNode = result.Root;
            PopulateDetails(result.Root);
            PopulateDuplicates(_allFilesSorted);
            RenderNode(result.Root);
            StatusTextBlock.Text = $"Scan complete: {result.Root.FileCount:N0} files in {result.Elapsed.TotalSeconds:0.0}s";
            SummaryTextBlock.Text = result.Root.SizeText;
            SdkStatusTextBlock.Text = $"{result.TotalResults:N0} SDK results";
            UpdateDriveSummary(rootPath, result.Root);
            ExpandRootItem();
            ShowSearchCompleteNotificationIfNeeded(rootPath, result);
        }
        catch (OperationCanceledException)
        {
            AppLogger.Warning($"Scan cancelled by request; path='{rootPath}'");
            StatusTextBlock.Text = "Scan cancelled";
            SummaryTextBlock.Text = string.Empty;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Scan failed; path='{rootPath}'", ex);
            StatusTextBlock.Text = ex.Message;
            SummaryTextBlock.Text = string.Empty;
            SdkStatusTextBlock.Text = "SDK scan failed";
            System.Windows.MessageBox.Show(this, ex.Message, "Scan failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            AppLogger.Info("Scan finally block entered; disposing cancellation source and resetting UI state");
            _scanCancellation?.Dispose();
            _scanCancellation = null;
            SetScanning(false);
        }
    }

    private void SetScanning(bool isScanning)
    {
        AppLogger.Info($"SetScanning({isScanning})");
        ScanButton.IsEnabled = !isScanning;
        BrowseButton.IsEnabled = !isScanning;
        DriveComboBox.IsEnabled = !isScanning;
        RootPathTextBox.IsEnabled = !isScanning;
        CancelButton.IsEnabled = isScanning;
        if (isScanning)
        {
            ScanProgressBar.IsIndeterminate = true;
            ScanProgressBar.Value = 0;
            StatusTextBlock.Text = "Querying Everything SDK";
            SummaryTextBlock.Text = string.Empty;
        }
        else
        {
            ScanProgressBar.IsIndeterminate = false;
        }
    }

    private void ConfigureNotifications()
    {
        try
        {
            var iconPath = IoPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "AppIcon.ico");
            _notifyIcon.Icon = System.IO.File.Exists(iconPath)
                ? new System.Drawing.Icon(iconPath)
                : System.Drawing.SystemIcons.Application;
            _notifyIcon.Text = "Everything Disk Usage";
            _notifyIcon.Visible = true;
            _notifyIcon.BalloonTipClicked += (_, _) => ActivateFromNotification();
            _notifyIcon.DoubleClick += (_, _) => ActivateFromNotification();
            AppLogger.Info("Windows notification icon configured");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to configure Windows notification icon", ex);
        }
    }

    private void ShowSearchCompleteNotificationIfNeeded(string rootPath, ScanResult result)
    {
        if (IsActive)
        {
            AppLogger.Info($"Search completion notification skipped because main window is focused; rootPath='{rootPath}'");
            return;
        }

        try
        {
            var title = "Everything Disk Usage scan complete";
            var message = $"{rootPath}\n{result.Root.FileCount:N0} files, {result.Root.SizeText}, {result.Elapsed.TotalSeconds:0.0}s";
            AppLogger.Info($"Showing search completion notification; rootPath='{rootPath}', files={result.Root.FileCount}, bytes={result.Root.SizeBytes}, elapsedMs={result.Elapsed.TotalMilliseconds:0}, isActive={IsActive}");
            _notifyIcon.ShowBalloonTip(8000, title, message, Forms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to show search completion notification; rootPath='{rootPath}'", ex);
        }
    }

    private void ActivateFromNotification()
    {
        AppLogger.Info("Notification activation requested");
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Focus();
    }

    private void UpdateProgress(ScanProgress progress)
    {
        LogUiProgress(progress);

        if (progress.TotalResults > 0)
        {
            ScanProgressBar.IsIndeterminate = false;
            ScanProgressBar.Maximum = progress.TotalResults;
            ScanProgressBar.Value = Math.Min(progress.FilesProcessed, progress.TotalResults);
            SummaryTextBlock.Text = $"{progress.FilesProcessed:N0} / {progress.TotalResults:N0}";
        }

        StatusTextBlock.Text = $"Scanning {DirectoryUsageNode.FormatBytes(progress.BytesProcessed)}";
    }

    private void UsageTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is DirectoryUsageNode node)
        {
            AppLogger.Info($"Tree selection changed; name='{node.DisplayName}', path='{node.FullPath}', sizeBytes={node.SizeBytes}, files={node.FileCount}, folders={node.FolderCount}");
            _selectedNode = node;
            RenderNode(node);
            SelectDirectoryDetailsNode(node);
        }
    }

    private async void UsageTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualAncestor<TreeViewItem>(e.OriginalSource as DependencyObject) is not { } treeItem || treeItem.DataContext is not DirectoryUsageNode node)
        {
            return;
        }

        treeItem.IsSelected = true;
        treeItem.Focus();
        e.Handled = true;
        await ShowShellContextMenuAsync(node.FullPath, ShellItemKind.Directory, e.GetPosition(this), "tree");
    }

    private void DirectoryDetailsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DirectoryDetailsGrid.SelectedItem is DirectoryUsageNode node)
        {
            AppLogger.Info($"Directory details selection changed; name='{node.DisplayName}', path='{node.FullPath}', sizeBytes={node.SizeBytes}, files={node.FileCount}, folders={node.FolderCount}");
            _selectedNode = node;
            RenderNode(node);
            UpdateVisibleFiles(node);
        }
    }

    private async void DirectoryDetailsGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is not { } row || row.Item is not DirectoryUsageNode node)
        {
            return;
        }

        DirectoryDetailsGrid.SelectedItem = node;
        row.Focus();
        e.Handled = true;
        await ShowShellContextMenuAsync(node.FullPath, ShellItemKind.Directory, e.GetPosition(this), "folder details");
    }

    private async void FileDetailsGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is not { } row || row.Item is not FileDetailRow { ShellItemPath: { Length: > 0 } shellItemPath } fileRow)
        {
            return;
        }

        FileDetailsGrid.SelectedItem = fileRow;
        row.Focus();
        e.Handled = true;
        await ShowShellContextMenuAsync(shellItemPath, ShellItemKind.File, e.GetPosition(this), "file details");
    }

    private async void DuplicatesGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is not { } row ||
            row.Item is not DuplicateFileRow { ShellItemPath: { Length: > 0 } shellItemPath } fileRow)
        {
            return;
        }

        DuplicatesGrid.SelectedItem = fileRow;
        row.Focus();
        e.Handled = true;
        await ShowShellContextMenuAsync(shellItemPath, ShellItemKind.File, e.GetPosition(this), "duplicates");
    }

    private async Task ShowShellContextMenuAsync(string path, ShellItemKind itemKind, WpfPoint localPoint, string source)
    {
        if (_scanCancellation is not null)
        {
            AppLogger.Warning($"Shell context menu skipped during active scan; source='{source}', path='{path}'");
            return;
        }

        if (_isUpdatingScanView)
        {
            AppLogger.Warning($"Shell context menu skipped while scan view is updating; source='{source}', path='{path}'");
            return;
        }

        if (!TryGetExistingShellItem(path, itemKind, out var shellPath))
        {
            AppLogger.Warning($"Shell context menu target no longer exists; source='{source}', path='{path}'");
            System.Windows.MessageBox.Show(this, "That item no longer exists. Refreshing the current scan view.", "Item not found", MessageBoxButton.OK, MessageBoxImage.Information);
            await RebuildUiFromCurrentFilesAsync(_selectedNode?.FullPath, "Removed stale item from view");
            return;
        }

        try
        {
            var screenPoint = PointToScreen(localPoint);
            var windowHandle = new WindowInteropHelper(this).Handle;
            AppLogger.Info($"Showing shell context menu; source='{source}', kind={itemKind}, path='{shellPath}', x={screenPoint.X:0}, y={screenPoint.Y:0}");
            var result = ShellContextMenu.Show(
                windowHandle,
                shellPath,
                (int)Math.Round(screenPoint.X),
                (int)Math.Round(screenPoint.Y),
                command => !IsRenameCommand(command.Verb, command.MenuText));

            if (!result.CommandSelected)
            {
                AppLogger.Debug($"Shell context menu dismissed without command; source='{source}', path='{shellPath}'");
                return;
            }

            AppLogger.Info($"Shell context menu command selected; source='{source}', path='{shellPath}', verb='{result.Verb}', menuText='{result.MenuText}', shellInvoked={result.ShellInvoked}");

            if (IsRenameCommand(result.Verb, result.MenuText))
            {
                await RenameShellItemAsync(shellPath, itemKind);
                return;
            }

            if (IsDeleteCommand(result.Verb, result.MenuText))
            {
                await RefreshAfterDeleteCommandAsync(shellPath, itemKind);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Shell context menu failed; source='{source}', path='{path}'", ex);
            System.Windows.MessageBox.Show(this, ex.Message, "Shell menu failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RefreshAfterDeleteCommandAsync(string deletedPath, ShellItemKind itemKind)
    {
        var previousSelectionPath = _selectedNode?.FullPath;
        await WaitForShellFileOperationAsync(deletedPath, itemKind, expectedExists: false);

        if (PathExists(deletedPath, itemKind))
        {
            AppLogger.Info($"Delete command completed but target still exists; no UI change applied; path='{deletedPath}', kind={itemKind}");
            return;
        }

        var beforeCount = _allFilesSorted.Count;
        _allFilesSorted = SortFiles(_allFilesSorted
            .Where(file => !IsFileWithinShellItem(file.FullPath, deletedPath, itemKind)))
            .ToList();

        var removedCount = beforeCount - _allFilesSorted.Count;
        AppLogger.Info($"Delete reflected in scan model; path='{deletedPath}', kind={itemKind}, removedFiles={removedCount}");
        await RebuildUiFromCurrentFilesAsync(ChooseSelectionAfterDelete(previousSelectionPath, deletedPath, itemKind), $"Deleted {removedCount:N0} indexed file{(removedCount == 1 ? string.Empty : "s")}");
    }

    private async Task RenameShellItemAsync(string oldPath, ShellItemKind itemKind)
    {
        var previousSelectionPath = _selectedNode?.FullPath;
        var normalizedOldPath = NormalizeShellItemPath(oldPath, itemKind);
        var oldName = GetShellItemName(normalizedOldPath, itemKind);
        if (string.IsNullOrWhiteSpace(oldName))
        {
            AppLogger.Warning($"Rename skipped because target name could not be resolved; path='{oldPath}', kind={itemKind}");
            return;
        }

        var newName = ShowRenameDialog(oldName);
        if (string.IsNullOrWhiteSpace(newName) || newName.Equals(oldName, StringComparison.Ordinal))
        {
            AppLogger.Info($"Rename cancelled or unchanged; path='{oldPath}', kind={itemKind}");
            return;
        }

        if (newName.IndexOfAny(IoPath.GetInvalidFileNameChars()) >= 0)
        {
            System.Windows.MessageBox.Show(this, "The new name contains characters Windows does not allow in file or folder names.", "Rename failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var parentPath = GetShellItemParentPath(normalizedOldPath, itemKind);
        if (string.IsNullOrWhiteSpace(parentPath))
        {
            AppLogger.Warning($"Rename skipped because target parent could not be resolved; path='{oldPath}', kind={itemKind}");
            return;
        }

        var newPath = IoPath.Combine(parentPath, newName);
        if (PathExists(newPath, itemKind))
        {
            System.Windows.MessageBox.Show(this, "An item with that name already exists in this folder.", "Rename failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            AppLogger.Info($"Renaming shell item; oldPath='{normalizedOldPath}', newPath='{newPath}', kind={itemKind}");
            if (itemKind == ShellItemKind.Directory)
            {
                Directory.Move(normalizedOldPath, newPath);
            }
            else
            {
                System.IO.File.Move(normalizedOldPath, newPath);
            }

            await WaitForShellFileOperationAsync(newPath, itemKind, expectedExists: true);
            await ApplyRenameToCurrentScanAsync(normalizedOldPath, newPath, itemKind, previousSelectionPath);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Rename failed; oldPath='{normalizedOldPath}', newPath='{newPath}', kind={itemKind}", ex);
            System.Windows.MessageBox.Show(this, ex.Message, "Rename failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task ApplyRenameToCurrentScanAsync(string oldPath, string newPath, ShellItemKind itemKind, string? previousSelectionPath)
    {
        if (_currentRootPath is null)
        {
            AppLogger.Warning($"Rename model refresh skipped because there is no current root; oldPath='{oldPath}', newPath='{newPath}', kind={itemKind}");
            return;
        }

        var normalizedOldPath = NormalizeShellItemPath(oldPath, itemKind);
        var normalizedNewPath = NormalizeShellItemPath(newPath, itemKind);
        var updatedFiles = new List<FileUsageItem>(_allFilesSorted.Count);
        var changedFileCount = 0;

        foreach (var file in _allFilesSorted)
        {
            if (!TryMapRenamedFile(file, normalizedOldPath, normalizedNewPath, itemKind, out var updatedFile))
            {
                updatedFiles.Add(file);
                continue;
            }

            updatedFiles.Add(updatedFile);
            changedFileCount++;
        }

        if (itemKind == ShellItemKind.Directory && IsSameDirectory(_currentRootPath, normalizedOldPath))
        {
            _currentRootPath = NormalizeDirectoryScope(normalizedNewPath);
            RootPathTextBox.Text = _currentRootPath;
            AppLogger.Info($"Current root path updated after root folder rename; newRoot='{_currentRootPath}'");
        }

        _allFilesSorted = SortFiles(updatedFiles).ToList();
        var selectionPath = TransformDirectoryPathAfterRename(previousSelectionPath, normalizedOldPath, normalizedNewPath, itemKind);
        AppLogger.Info($"Rename reflected in scan model; oldPath='{normalizedOldPath}', newPath='{normalizedNewPath}', kind={itemKind}, changedFiles={changedFileCount}");
        await RebuildUiFromCurrentFilesAsync(selectionPath, $"Renamed {GetShellItemName(normalizedNewPath, itemKind)}");
    }

    private async Task RebuildUiFromCurrentFilesAsync(string? preferredSelectionPath, string statusText)
    {
        if (_currentRootPath is null)
        {
            return;
        }

        if (_isUpdatingScanView)
        {
            AppLogger.Warning($"RebuildUiFromCurrentFilesAsync skipped because another update is already running; preferredSelection='{preferredSelectionPath ?? string.Empty}', status='{statusText}'");
            return;
        }

        var rootPath = _currentRootPath;
        var files = _allFilesSorted.ToList();
        _isUpdatingScanView = true;
        ScanProgressBar.IsIndeterminate = true;
        StatusTextBlock.Text = "Updating scan view";

        try
        {
            var snapshot = await Task.Run(() => BuildScanViewSnapshot(rootPath, files));
            var root = snapshot.Root;
            _currentRoot = root;
            _allFilesSorted = snapshot.Files;

            _treeRoots.Clear();
            _treeRoots.Add(root);
            PopulateDetails(root, selectRoot: false);

            var selectedNode = FindDirectoryByPath(root, preferredSelectionPath) ?? FindNearestExistingParentNode(root, preferredSelectionPath) ?? root;
            _selectedNode = selectedNode;
            RenderNode(selectedNode);
            SelectDirectoryDetailsNode(selectedNode);

            if (!ExpandAndSelectTreeNode(selectedNode))
            {
                ExpandRootItem();
            }

            StatusTextBlock.Text = statusText;
            SummaryTextBlock.Text = root.SizeText;
            UpdateDriveSummary(root.FullPath, root);
            ApplyDuplicateSnapshot(snapshot.Duplicates);
            AppLogger.Info($"UI rebuilt from current file model; root='{root.FullPath}', files={root.FileCount}, bytes={root.SizeBytes}, preferredSelection='{preferredSelectionPath ?? string.Empty}', selected='{selectedNode.FullPath}', status='{statusText}'");
        }
        finally
        {
            ScanProgressBar.IsIndeterminate = false;
            _isUpdatingScanView = false;
        }
    }

    private void LegendItems_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LegendItems.SelectedItem is not PieSlice slice)
        {
            return;
        }

        LegendItems.SelectedItem = null;

        if (slice.Node is not DirectoryUsageNode node)
        {
            AppLogger.Info($"Legend slice clicked without a folder target; label='{slice.Label}'");
            return;
        }

        AppLogger.Info($"Legend slice clicked; label='{slice.Label}', targetPath='{node.FullPath}', sizeBytes={node.SizeBytes}, files={node.FileCount}, folders={node.FolderCount}");
        SelectFolderFromPieSlice(node);
    }

    private void PieCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        AppLogger.Debug($"PieCanvas size changed; width={PieCanvas.ActualWidth:0.##}, height={PieCanvas.ActualHeight:0.##}, selectedNode='{_selectedNode?.FullPath ?? string.Empty}'");
        if (_selectedNode is not null)
        {
            RenderNode(_selectedNode);
        }
    }

    private void RenderNode(DirectoryUsageNode node)
    {
        AppLogger.Debug($"RenderNode starting; name='{node.DisplayName}', path='{node.FullPath}', sizeBytes={node.SizeBytes}, childCount={node.Children.Count}, directFileBytes={node.DirectFileSizeBytes}");
        PieBackButton.Visibility = node.Parent is null ? Visibility.Collapsed : Visibility.Visible;
        PieBackButton.ToolTip = node.Parent is null ? null : $"Show {node.Parent.DisplayName}";
        SelectedNameTextBlock.Text = node.DisplayName;
        SelectedPathTextBlock.Text = node.FullPath;
        SelectedSizeTextBlock.Text = node.SizeText;
        SelectedFilesTextBlock.Text = node.FileCountText;
        SelectedFoldersTextBlock.Text = node.FolderCountText;

        var slices = BuildSlices(node).ToList();
        LegendItems.ItemsSource = slices;
        DrawPie(slices);
        AppLogger.Debug($"RenderNode completed; name='{node.DisplayName}', slices={slices.Count}");
    }

    private void PieBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode?.Parent is not { } parent)
        {
            AppLogger.Debug("Pie back button clicked without a parent node");
            return;
        }

        AppLogger.Info($"Pie back button clicked; current='{_selectedNode.FullPath}', parent='{parent.FullPath}'");
        SelectFolderFromPieSlice(parent);
    }

    private ScanViewSnapshot BuildScanViewSnapshot(string rootPath, IReadOnlyList<FileUsageItem> files)
    {
        using var operation = AppLogger.TimedOperation($"BuildScanViewSnapshot; rootPath='{rootPath}'; fileCount={files.Count}");
        var normalizedRoot = NormalizeDirectoryScope(rootPath);
        var existingFiles = SortFiles(files
            .Where(file => IsInsideRoot(file.FullPath, normalizedRoot) && System.IO.File.Exists(file.FullPath)))
            .ToList();
        var root = BuildRootFromFiles(normalizedRoot, existingFiles);
        var duplicates = BuildDuplicateSnapshot(existingFiles);
        AppLogger.Info($"Scan view snapshot built; root='{root.FullPath}', files={existingFiles.Count}, bytes={root.SizeBytes}, duplicateGroups={duplicates.TotalGroups}");
        return new ScanViewSnapshot(root, existingFiles, duplicates);
    }

    private void PopulateDetails(DirectoryUsageNode root, bool selectRoot = true)
    {
        using var operation = AppLogger.TimedOperation($"PopulateDetails; root='{root.FullPath}'");
        _directoryDetails.Clear();
        foreach (var node in FlattenDirectories(root))
        {
            _directoryDetails.Add(node);
        }

        FolderDetailsSummaryTextBlock.Text = $"{_directoryDetails.Count:N0} folders";
        AppLogger.Info($"Directory details populated; folderRows={_directoryDetails.Count}");
        if (selectRoot)
        {
            SelectDirectoryDetailsNode(root);
        }
    }

    private void PopulateDuplicates(IReadOnlyList<FileUsageItem> files)
    {
        using var operation = AppLogger.TimedOperation($"PopulateDuplicates; fileCount={files.Count}");
        ApplyDuplicateSnapshot(BuildDuplicateSnapshot(files));
    }

    private static DuplicateRowsSnapshot BuildDuplicateSnapshot(IReadOnlyList<FileUsageItem> files)
    {
        const int MaxGroups = 500;
        var rows = new List<DuplicateFileRow>();
        if (files.Count == 0)
        {
            return new DuplicateRowsSnapshot(rows, files.Count, TotalGroups: 0, TotalWastedBytes: 0L, MaxGroups);
        }

        var allGroups = files
            .Where(f => f.SizeBytes > 0)
            .GroupBy(f => (Name: f.Name.ToLowerInvariant(), f.SizeBytes))
            .Where(g => g.Count() >= 2)
            .Select(g =>
            {
                var groupFiles = g.OrderBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase).ToList();
                return new
                {
                    Name = groupFiles[0].Name,
                    g.Key.SizeBytes,
                    Files = groupFiles,
                    WastedBytes = (long)(groupFiles.Count - 1) * g.Key.SizeBytes
                };
            })
            .OrderByDescending(g => g.WastedBytes)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalGroups = allGroups.Count;
        var totalWasted = allGroups.Sum(g => g.WastedBytes);

        foreach (var group in allGroups.Take(MaxGroups))
        {
            rows.Add(new DuplicateFileRow(
                group.Name,
                $"{group.Files.Count} copies",
                ShellItemPath: null,
                group.Files.Count,
                group.SizeBytes,
                group.WastedBytes,
                IsGroup: true));

            foreach (var file in group.Files)
            {
                rows.Add(new DuplicateFileRow(
                    file.Name,
                    file.DirectoryPath,
                    file.FullPath,
                    CopyCount: 1,
                    file.SizeBytes,
                    WastedBytes: 0L,
                    IsGroup: false));
            }
        }

        return new DuplicateRowsSnapshot(rows, files.Count, totalGroups, totalWasted, MaxGroups);
    }

    private void ApplyDuplicateSnapshot(DuplicateRowsSnapshot snapshot)
    {
        _duplicateRows.Clear();
        foreach (var row in snapshot.Rows)
        {
            _duplicateRows.Add(row);
        }

        if (snapshot.SourceFileCount == 0)
        {
            DuplicatesSummaryTextBlock.Text = string.Empty;
            return;
        }

        DuplicatesSummaryTextBlock.Text = snapshot.TotalGroups == 0
            ? "No duplicates found"
            : snapshot.TotalGroups > snapshot.MaxGroups
                ? $"Top {snapshot.MaxGroups:N0} of {snapshot.TotalGroups:N0} groups · {DirectoryUsageNode.FormatBytes(snapshot.TotalWastedBytes)} wasted"
                : $"{snapshot.TotalGroups:N0} group{(snapshot.TotalGroups == 1 ? string.Empty : "s")} · {DirectoryUsageNode.FormatBytes(snapshot.TotalWastedBytes)} wasted";

        AppLogger.Info($"Duplicates populated; totalGroups={snapshot.TotalGroups}, shownGroups={Math.Min(snapshot.TotalGroups, snapshot.MaxGroups)}, totalWastedBytes={snapshot.TotalWastedBytes}");
    }

    private void SelectDirectoryDetailsNode(DirectoryUsageNode node)
    {
        AppLogger.Debug($"SelectDirectoryDetailsNode; path='{node.FullPath}', alreadySelected={DirectoryDetailsGrid.SelectedItem == node}");
        if (DirectoryDetailsGrid.SelectedItem == node)
        {
            UpdateVisibleFiles(node);
            return;
        }

        DirectoryDetailsGrid.SelectedItem = node;
        DirectoryDetailsGrid.ScrollIntoView(node);
    }

    private void UpdateVisibleFiles(DirectoryUsageNode node)
    {
        using var operation = AppLogger.TimedOperation($"UpdateVisibleFiles; scope='{node.FullPath}'; nodeFiles={node.FileCount}", AppLogLevel.Debug);
        _fileDetails.Clear();

        if (_allFilesSorted.Count == 0)
        {
            FileDetailsSummaryTextBlock.Text = string.Empty;
            AppLogger.Debug("UpdateVisibleFiles skipped because all-files cache is empty");
            return;
        }

        var scopePath = NormalizeDirectoryScope(node.FullPath);
        var scopedFiles = _allFilesSorted
            .Where(file => file.FullPath.StartsWith(scopePath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        AppLogger.Debug($"Scoped files resolved; scopePath='{scopePath}', scopedFileCount={scopedFiles.Count}");

        var fileGroups = scopedFiles
            .GroupBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var groupFiles = group
                    .OrderByDescending(file => file.SizeBytes)
                    .ThenBy(file => file.DirectoryPath, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new
                {
                    Name = group.Key,
                    Files = groupFiles,
                    SizeBytes = groupFiles.Sum(file => file.SizeBytes),
                    LastModifiedUtc = MaxDate(groupFiles.Select(file => file.LastModifiedUtc)),
                    LastAccessedUtc = MaxDate(groupFiles.Select(file => file.LastAccessedUtc))
                };
            })
            .OrderByDescending(group => group.SizeBytes)
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        AppLogger.Debug($"File groups built; groupCount={fileGroups.Count}");

        foreach (var group in fileGroups)
        {
            if (_fileDetails.Count >= MaxVisibleFileRows)
            {
                break;
            }

            var pathText = group.Files.Count > 1 ? "[multiple]" : group.Files[0].DirectoryPath;
            _fileDetails.Add(FileDetailRow.FromGroup(
                group.Name,
                pathText,
                group.Files.Count,
                group.SizeBytes,
                group.LastModifiedUtc,
                group.LastAccessedUtc));

            if (group.Files.Count <= 1)
            {
                continue;
            }

            foreach (var file in group.Files)
            {
                if (_fileDetails.Count >= MaxVisibleFileRows)
                {
                    break;
                }

                _fileDetails.Add(FileDetailRow.FromFile(file));
            }
        }

        FileDetailsSummaryTextBlock.Text = _fileDetails.Count >= MaxVisibleFileRows && node.FileCount > _fileDetails.Count
            ? $"Showing {_fileDetails.Count:N0} rows from {node.FileCount:N0} files"
            : $"{node.FileCount:N0} files";
        AppLogger.Info($"File details populated; scope='{node.FullPath}', groupCount={fileGroups.Count}, visibleRows={_fileDetails.Count}, nodeFiles={node.FileCount}");
    }

    private void LogUiProgress(ScanProgress progress)
    {
        var nowUtc = DateTime.UtcNow;
        var filesDelta = progress.FilesProcessed - _lastUiProgressLoggedFiles;
        if (progress.FilesProcessed == 0 || filesDelta >= 10_000 || nowUtc - _lastUiProgressLogUtc >= TimeSpan.FromSeconds(2))
        {
            AppLogger.Info($"UI scan progress; filesProcessed={progress.FilesProcessed}; totalResults={progress.TotalResults}; bytesProcessed={progress.BytesProcessed}; bytesText='{DirectoryUsageNode.FormatBytes(progress.BytesProcessed)}'");
            _lastUiProgressLoggedFiles = progress.FilesProcessed;
            _lastUiProgressLogUtc = nowUtc;
        }
    }

    private static DateTime? MaxDate(IEnumerable<DateTime?> dates)
    {
        DateTime? maxDate = null;
        foreach (var date in dates)
        {
            if (date is not null && (maxDate is null || date.Value > maxDate.Value))
            {
                maxDate = date;
            }
        }

        return maxDate;
    }

    private static IEnumerable<DirectoryUsageNode> FlattenDirectories(DirectoryUsageNode root)
    {
        AppLogger.Debug($"FlattenDirectories starting; root='{root.FullPath}'");
        var pendingNodes = new Stack<DirectoryUsageNode>();
        pendingNodes.Push(root);

        while (pendingNodes.Count > 0)
        {
            var node = pendingNodes.Pop();
            yield return node;

            for (var childIndex = node.Children.Count - 1; childIndex >= 0; childIndex--)
            {
                pendingNodes.Push(node.Children[childIndex]);
            }
        }
    }

    private static IEnumerable<PieSlice> BuildSlices(DirectoryUsageNode node)
    {
        AppLogger.Debug($"BuildSlices starting; path='{node.FullPath}', sizeBytes={node.SizeBytes}, childCount={node.Children.Count}, directFileSizeBytes={node.DirectFileSizeBytes}");
        if (node.SizeBytes <= 0)
        {
            yield break;
        }

        var pieces = node.Children
            .Where(child => child.SizeBytes > 0)
            .Select(child => (Label: child.DisplayName, child.SizeBytes, Node: (DirectoryUsageNode?)child))
            .ToList();

        if (node.DirectFileSizeBytes > 0)
        {
            pieces.Add(("Files in this folder", node.DirectFileSizeBytes, null));
        }

        var orderedPieces = pieces
            .OrderByDescending(piece => piece.SizeBytes)
            .ToList();

        var visiblePieces = orderedPieces.Take(9).ToList();
        var otherBytes = orderedPieces.Skip(9).Sum(piece => piece.SizeBytes);
        if (otherBytes > 0)
        {
            visiblePieces.Add(("Other", otherBytes, null));
        }

        for (var i = 0; i < visiblePieces.Count; i++)
        {
            var brush = new SolidColorBrush(SliceColors[i % SliceColors.Length]);
            brush.Freeze();
            var percent = visiblePieces[i].SizeBytes * 100d / node.SizeBytes;
            var explorerNode = visiblePieces[i].Node ?? node;
            yield return new PieSlice(visiblePieces[i].Label, visiblePieces[i].SizeBytes, percent, brush, visiblePieces[i].Node, explorerNode);
        }
    }

    private void DrawPie(IReadOnlyList<PieSlice> slices)
    {
        AppLogger.Debug($"DrawPie starting; slices={slices.Count}, canvasWidth={PieCanvas.ActualWidth:0.##}, canvasHeight={PieCanvas.ActualHeight:0.##}");
        PieCanvas.Children.Clear();

        var total = slices.Sum(slice => slice.SizeBytes);
        if (total <= 0 || PieCanvas.ActualWidth <= 1 || PieCanvas.ActualHeight <= 1)
        {
            EmptyPieTextBlock.Visibility = Visibility.Visible;
            AppLogger.Debug($"DrawPie skipped; total={total}, canvasWidth={PieCanvas.ActualWidth:0.##}, canvasHeight={PieCanvas.ActualHeight:0.##}");
            return;
        }

        EmptyPieTextBlock.Visibility = Visibility.Collapsed;
    var radius = Math.Max(24, Math.Min(PieCanvas.ActualWidth, PieCanvas.ActualHeight) * 0.45);
        var center = new WpfPoint(PieCanvas.ActualWidth / 2, PieCanvas.ActualHeight / 2);
        var startAngle = -90d;

        foreach (var slice in slices)
        {
            var sweepAngle = Math.Max(0.4, slice.SizeBytes * 360d / total);
            if (sweepAngle >= 359.6)
            {
                var ellipse = new Ellipse
                {
                    Width = radius * 2,
                    Height = radius * 2,
                    Fill = slice.Fill,
                    Stroke = MediaBrushes.White,
                    StrokeThickness = 1.5
                };
                ConfigurePieSliceShape(ellipse, slice);
                PieCanvas.Children.Add(ellipse);
                Canvas.SetLeft(ellipse, center.X - radius);
                Canvas.SetTop(ellipse, center.Y - radius);
            }
            else
            {
                var path = new PathShape
                {
                    Data = CreateSliceGeometry(center, radius, startAngle, sweepAngle),
                    Fill = slice.Fill,
                    Stroke = MediaBrushes.White,
                    StrokeThickness = 1.5
                };
                ConfigurePieSliceShape(path, slice);
                PieCanvas.Children.Add(path);
            }

            DrawPieSliceLabel(slice, center, radius, startAngle, sweepAngle);

            startAngle += sweepAngle;
        }

        AppLogger.Debug($"DrawPie completed; totalBytes={total}, drawnChildren={PieCanvas.Children.Count}");
    }

    private void DrawPieSliceLabel(PieSlice slice, WpfPoint center, double radius, double startAngle, double sweepAngle)
    {
        if (sweepAngle < 8)
        {
            return;
        }

        var maxWidth = Math.Max(56, radius * Math.Max(0.3, Math.Sin(sweepAngle * Math.PI / 360d) * 1.45));
        var text = new TextBlock
        {
            Text = slice.Label,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = CreateSliceLabelForeground(slice.Fill),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Width = maxWidth,
            MaxWidth = maxWidth,
            IsHitTestVisible = false
        };

        text.Measure(new System.Windows.Size(maxWidth, double.PositiveInfinity));
        var desiredSize = text.DesiredSize;
        var midAngle = startAngle + (sweepAngle / 2d);
        var labelRadius = radius * (sweepAngle >= 30 ? 0.58 : 0.68);
        var anchorPoint = PointOnCircle(center, labelRadius, midAngle);

        PieCanvas.Children.Add(text);
        Canvas.SetLeft(text, anchorPoint.X - (desiredSize.Width / 2d));
        Canvas.SetTop(text, anchorPoint.Y - (desiredSize.Height / 2d));
    }

    private static MediaBrush CreateSliceLabelForeground(MediaBrush fill)
    {
        if (fill is not SolidColorBrush { Color: var color })
        {
            return MediaBrushes.White;
        }

        var luminance = (0.2126 * color.ScR) + (0.7152 * color.ScG) + (0.0722 * color.ScB);
        return luminance > 0.5 ? MediaBrushes.Black : MediaBrushes.White;
    }

    private void ConfigurePieSliceShape(Shape shape, PieSlice slice)
    {
        shape.Tag = slice;
        shape.ToolTip = $"{slice.Label}: {slice.SizeText} ({slice.PercentText})";
        shape.ContextMenu = CreatePieSliceContextMenu(slice);

        if (!slice.IsSelectable)
        {
            return;
        }

        shape.Cursor = System.Windows.Input.Cursors.Hand;
        shape.MouseLeftButtonUp += PieSliceShape_MouseLeftButtonUp;
    }

    private System.Windows.Controls.ContextMenu CreatePieSliceContextMenu(PieSlice slice)
    {
        var menu = new System.Windows.Controls.ContextMenu();
        var openItem = new System.Windows.Controls.MenuItem
        {
            Header = "Open in File Explorer",
            IsEnabled = slice.CanOpenInExplorer,
            Tag = slice
        };
        openItem.Click += OpenPieSliceInExplorer_Click;
        menu.Items.Add(openItem);
        return menu;
    }

    private void OpenPieSliceInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PieSlice { ExplorerNode: DirectoryUsageNode explorerNode } slice })
        {
            AppLogger.Warning("Open in File Explorer clicked without a usable slice target");
            return;
        }

        OpenInFileExplorer(explorerNode.FullPath, $"pie slice '{slice.Label}'");
    }

    private void PieSliceShape_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PieSlice { Node: DirectoryUsageNode node } slice })
        {
            return;
        }

        AppLogger.Info($"Pie slice clicked; label='{slice.Label}', targetPath='{node.FullPath}', sizeBytes={node.SizeBytes}, files={node.FileCount}, folders={node.FolderCount}");
        e.Handled = true;

        SelectFolderFromPieSlice(node);
    }

    private void SelectFolderFromPieSlice(DirectoryUsageNode node)
    {
        if (!ExpandAndSelectTreeNode(node))
        {
            AppLogger.Warning($"Pie slice target could not be selected in TreeView; rendering node directly; targetPath='{node.FullPath}'");
            _selectedNode = node;
            RenderNode(node);
            SelectDirectoryDetailsNode(node);
        }
    }

    private static void OpenInFileExplorer(string folderPath, string source)
    {
        try
        {
            AppLogger.Info($"Opening folder in File Explorer; source={source}; folderPath='{folderPath}'");
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folderPath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to open folder in File Explorer; source={source}; folderPath='{folderPath}'", ex);
            System.Windows.MessageBox.Show($"Could not open File Explorer for:\n{folderPath}", "Open in File Explorer failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool ExpandAndSelectTreeNode(DirectoryUsageNode node)
    {
        using var operation = AppLogger.TimedOperation($"ExpandAndSelectTreeNode; target='{node.FullPath}'", AppLogLevel.Debug);

        var path = BuildNodePath(node).ToList();
        if (path.Count == 0)
        {
            AppLogger.Warning($"ExpandAndSelectTreeNode failed because node path was empty; target='{node.FullPath}'");
            return false;
        }

        ItemsControl currentItemsControl = UsageTree;
        TreeViewItem? currentTreeViewItem = null;

        foreach (var pathNode in path)
        {
            currentItemsControl.UpdateLayout();
            var item = currentItemsControl.ItemContainerGenerator.ContainerFromItem(pathNode) as TreeViewItem;
            if (item is null)
            {
                currentItemsControl.UpdateLayout();
                item = currentItemsControl.ItemContainerGenerator.ContainerFromItem(pathNode) as TreeViewItem;
            }

            if (item is null)
            {
                AppLogger.Warning($"ExpandAndSelectTreeNode could not find TreeViewItem container; pathNode='{pathNode.FullPath}', target='{node.FullPath}'");
                return false;
            }

            item.IsExpanded = true;
            item.UpdateLayout();
            currentTreeViewItem = item;
            currentItemsControl = item;
        }

        if (currentTreeViewItem is null)
        {
            AppLogger.Warning($"ExpandAndSelectTreeNode reached end without a selected container; target='{node.FullPath}'");
            return false;
        }

        currentTreeViewItem.IsSelected = true;
        currentTreeViewItem.BringIntoView();
        currentTreeViewItem.Focus();
        AppLogger.Info($"Tree node selected from pie slice; target='{node.FullPath}', expandedPathDepth={path.Count}");
        return true;
    }

    private static IEnumerable<DirectoryUsageNode> BuildNodePath(DirectoryUsageNode node)
    {
        var path = new Stack<DirectoryUsageNode>();
        for (var current = node; current is not null; current = current.Parent)
        {
            path.Push(current);
        }

        return path;
    }

    private DirectoryUsageNode BuildRootFromFiles(string rootPath, IEnumerable<FileUsageItem> files)
    {
        var normalizedRoot = NormalizeDirectoryScope(rootPath);
        var root = new DirectoryUsageNode(GetRootDisplayName(normalizedRoot), normalizedRoot);

        foreach (var file in files)
        {
            TryAddFileToNodeTree(root, normalizedRoot, file);
        }

        root.FinalizeStats(root.SizeBytes);
        return root;
    }

    private static void TryAddFileToNodeTree(DirectoryUsageNode root, string normalizedRoot, FileUsageItem file)
    {
        if (!IsInsideRoot(file.FullPath, normalizedRoot))
        {
            return;
        }

        root.AddAggregateFile(file.SizeBytes, file.LastModifiedUtc, file.LastAccessedUtc);

        var relativeDirectory = IoPath.GetRelativePath(normalizedRoot, file.DirectoryPath);
        var current = root;
        if (!string.IsNullOrWhiteSpace(relativeDirectory) && relativeDirectory != ".")
        {
            foreach (var part in relativeDirectory.Split([IoPath.DirectorySeparatorChar, IoPath.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                var childPath = IoPath.Combine(current.FullPath, part);
                current = current.GetOrAddChild(part, childPath);
                current.AddAggregateFile(file.SizeBytes, file.LastModifiedUtc, file.LastAccessedUtc);
            }
        }

        current.AddDirectFile(file.SizeBytes);
    }

    private DirectoryUsageNode? FindDirectoryByPath(DirectoryUsageNode root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return FlattenDirectories(root).FirstOrDefault(node => IsSameDirectory(node.FullPath, path));
    }

    private DirectoryUsageNode? FindNearestExistingParentNode(DirectoryUsageNode root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var current = path;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var node = FindDirectoryByPath(root, current);
            if (node is not null)
            {
                return node;
            }

            var trimmed = current.TrimEnd(IoPath.DirectorySeparatorChar, IoPath.AltDirectorySeparatorChar);
            var parent = Directory.GetParent(trimmed);
            if (parent is null)
            {
                return null;
            }

            current = parent.FullName;
        }

        return null;
    }

    private static IEnumerable<FileUsageItem> SortFiles(IEnumerable<FileUsageItem> files)
    {
        return files
            .OrderByDescending(file => file.SizeBytes)
            .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryMapRenamedFile(FileUsageItem file, string oldPath, string newPath, ShellItemKind itemKind, out FileUsageItem updatedFile)
    {
        if (!IsFileWithinShellItem(file.FullPath, oldPath, itemKind))
        {
            updatedFile = file;
            return false;
        }

        var trimmedOldPath = itemKind == ShellItemKind.Directory ? TrimDirectoryPath(oldPath) : oldPath;
        var trimmedNewPath = itemKind == ShellItemKind.Directory ? TrimDirectoryPath(newPath) : newPath;
        var newFullPath = itemKind == ShellItemKind.Directory
            ? trimmedNewPath + file.FullPath[trimmedOldPath.Length..]
            : newPath;
        var directoryPath = IoPath.GetDirectoryName(newFullPath) ?? string.Empty;
        updatedFile = file with
        {
            Name = IoPath.GetFileName(newFullPath),
            FullPath = newFullPath,
            DirectoryPath = directoryPath
        };
        return true;
    }

    private static bool IsFileWithinShellItem(string filePath, string shellItemPath, ShellItemKind itemKind)
    {
        if (itemKind == ShellItemKind.File)
        {
            return string.Equals(IoPath.GetFullPath(filePath), IoPath.GetFullPath(shellItemPath), StringComparison.OrdinalIgnoreCase);
        }

        return IsInsideRoot(filePath, NormalizeDirectoryScope(shellItemPath));
    }

    private static string? TransformDirectoryPathAfterRename(string? path, string oldPath, string newPath, ShellItemKind itemKind)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (itemKind == ShellItemKind.File)
        {
            return path;
        }

        var oldScope = NormalizeDirectoryScope(oldPath);
        if (!IsInsideRoot(path, oldScope) && !IsSameDirectory(path, oldPath))
        {
            return path;
        }

        var trimmedOld = TrimDirectoryPath(oldPath);
        var trimmedNew = TrimDirectoryPath(newPath);
        return trimmedNew + TrimDirectoryPath(path)[trimmedOld.Length..];
    }

    private static string? ChooseSelectionAfterDelete(string? previousSelectionPath, string deletedPath, ShellItemKind itemKind)
    {
        if (string.IsNullOrWhiteSpace(previousSelectionPath))
        {
            return null;
        }

        if (itemKind == ShellItemKind.File)
        {
            return previousSelectionPath;
        }

        return IsInsideRoot(previousSelectionPath, NormalizeDirectoryScope(deletedPath)) || IsSameDirectory(previousSelectionPath, deletedPath)
            ? Directory.GetParent(TrimDirectoryPath(deletedPath))?.FullName
            : previousSelectionPath;
    }

    private static async Task WaitForShellFileOperationAsync(string path, ShellItemKind itemKind, bool expectedExists)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (PathExists(path, itemKind) == expectedExists)
            {
                return;
            }

            await Task.Delay(150);
        }
    }

    private static bool TryGetExistingShellItem(string path, ShellItemKind itemKind, out string existingPath)
    {
        existingPath = NormalizeShellItemPath(path, itemKind);

        return PathExists(existingPath, itemKind);
    }

    private static bool PathExists(string path, ShellItemKind itemKind)
    {
        return itemKind == ShellItemKind.Directory ? Directory.Exists(path) : System.IO.File.Exists(path);
    }

    private static string NormalizeShellItemPath(string path, ShellItemKind itemKind)
    {
        var fullPath = IoPath.GetFullPath(path);
        return itemKind == ShellItemKind.Directory
            ? TrimDirectoryPath(fullPath)
            : fullPath;
    }

    private static string GetShellItemName(string path, ShellItemKind itemKind)
    {
        var normalized = NormalizeShellItemPath(path, itemKind);
        return itemKind == ShellItemKind.Directory && IsDriveRoot(normalized)
            ? normalized
            : IoPath.GetFileName(normalized);
    }

    private static string? GetShellItemParentPath(string path, ShellItemKind itemKind)
    {
        var normalized = NormalizeShellItemPath(path, itemKind);
        return itemKind == ShellItemKind.Directory
            ? Directory.GetParent(normalized)?.FullName
            : IoPath.GetDirectoryName(normalized);
    }

    private static bool IsDeleteCommand(string verb, string menuText)
    {
        return verb.Equals("delete", StringComparison.OrdinalIgnoreCase)
            || menuText.Replace("&", string.Empty, StringComparison.Ordinal).Contains("delete", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRenameCommand(string verb, string menuText)
    {
        return verb.Equals("rename", StringComparison.OrdinalIgnoreCase)
            || menuText.Replace("&", string.Empty, StringComparison.Ordinal).Contains("rename", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameDirectory(string left, string right)
    {
        var normalizedLeft = TrimDirectoryPath(left);
        var normalizedRight = TrimDirectoryPath(right);
        return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimDirectoryPath(string path)
    {
        var fullPath = IoPath.GetFullPath(path);
        var root = IoPath.GetPathRoot(fullPath);
        if (!string.IsNullOrWhiteSpace(root) && string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        return fullPath.TrimEnd(IoPath.DirectorySeparatorChar, IoPath.AltDirectorySeparatorChar);
    }

    private static bool IsDriveRoot(string path)
    {
        var root = IoPath.GetPathRoot(path);
        return !string.IsNullOrWhiteSpace(root) && string.Equals(IoPath.GetFullPath(path), root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInsideRoot(string filePath, string normalizedRoot)
    {
        return IoPath.GetFullPath(filePath).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRootDisplayName(string normalizedRoot)
    {
        var trimmed = normalizedRoot.TrimEnd(IoPath.DirectorySeparatorChar, IoPath.AltDirectorySeparatorChar);
        return trimmed.EndsWith(':') ? normalizedRoot : IoPath.GetFileName(trimmed);
    }

    private static T? FindVisualAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private string? ShowRenameDialog(string currentName)
    {
        var owner = this;
        var input = new System.Windows.Controls.TextBox
        {
            Text = currentName,
            MinWidth = 360,
            Margin = new Thickness(0, 8, 0, 12)
        };
        input.SelectAll();

        var okButton = new System.Windows.Controls.Button
        {
            Content = "Rename",
            IsDefault = true,
            MinWidth = 84,
            Margin = new Thickness(8, 0, 0, 0)
        };
        var cancelButton = new System.Windows.Controls.Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 84,
            Margin = new Thickness(8, 0, 0, 0)
        };

        var buttonPanel = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        buttonPanel.Children.Add(okButton);
        buttonPanel.Children.Add(cancelButton);

        var panel = new StackPanel
        {
            Margin = new Thickness(16)
        };
        panel.Children.Add(new TextBlock { Text = "New name", Foreground = MediaBrushes.DimGray });
        panel.Children.Add(input);
        panel.Children.Add(buttonPanel);

        var dialog = new Window
        {
            Title = "Rename",
            Content = panel,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = false
        };
        okButton.Click += (_, _) => dialog.DialogResult = true;

        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }

    private static Geometry CreateSliceGeometry(WpfPoint center, double radius, double startAngle, double sweepAngle)
    {
        var startPoint = PointOnCircle(center, radius, startAngle);
        var endPoint = PointOnCircle(center, radius, startAngle + sweepAngle);

        var figure = new PathFigure
        {
            StartPoint = center,
            IsClosed = true
        };
        figure.Segments.Add(new LineSegment(startPoint, true));
        figure.Segments.Add(new ArcSegment(endPoint, new System.Windows.Size(radius, radius), 0, sweepAngle > 180, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(center, true));

        return new PathGeometry([figure]);
    }

    private static WpfPoint PointOnCircle(WpfPoint center, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180d;
        return new WpfPoint(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    private void ExpandRootItem()
    {
        AppLogger.Debug($"ExpandRootItem starting; rootCount={_treeRoots.Count}");
        UsageTree.UpdateLayout();
        if (_treeRoots.Count > 0 && UsageTree.ItemContainerGenerator.ContainerFromItem(_treeRoots[0]) is TreeViewItem item)
        {
            item.IsExpanded = true;
            item.IsSelected = true;
            AppLogger.Debug("ExpandRootItem completed; root expanded and selected");
        }
        else
        {
            AppLogger.Warning("ExpandRootItem could not resolve root TreeViewItem");
        }
    }

    private void UpdateDriveSummary(string path, DirectoryUsageNode? root)
    {
        try
        {
            AppLogger.Debug($"UpdateDriveSummary starting; path='{path}', hasRoot={root is not null}");
            var driveRoot = IoPath.GetPathRoot(IoPath.GetFullPath(path));
            if (driveRoot is null)
            {
                DriveSummaryTextBlock.Text = string.Empty;
                AppLogger.Warning($"UpdateDriveSummary could not resolve drive root; path='{path}'");
                return;
            }

            var drive = new DriveInfo(driveRoot);
            if (!drive.IsReady)
            {
                DriveSummaryTextBlock.Text = driveRoot;
                AppLogger.Warning($"UpdateDriveSummary drive not ready; driveRoot='{driveRoot}'");
                return;
            }

            var used = Math.Max(0, drive.TotalSize - drive.AvailableFreeSpace);
            var scanned = root is null ? string.Empty : $" | Scanned {root.SizeText}";
            DriveSummaryTextBlock.Text = $"{drive.Name} Total {DirectoryUsageNode.FormatBytes(drive.TotalSize)} | Used {DirectoryUsageNode.FormatBytes(used)} | Free {DirectoryUsageNode.FormatBytes(drive.AvailableFreeSpace)}{scanned}";
            AppLogger.Info($"Drive summary updated; drive='{drive.Name}', totalBytes={drive.TotalSize}, usedBytes={used}, freeBytes={drive.AvailableFreeSpace}, scannedBytes={root?.SizeBytes ?? 0}");
        }
        catch (Exception ex)
        {
            DriveSummaryTextBlock.Text = string.Empty;
            AppLogger.Error($"UpdateDriveSummary failed; path='{path}'", ex);
        }
    }

    private static string EnsureTrailingSeparator(string path)
    {
        var fullPath = IoPath.GetFullPath(path);
        return fullPath.EndsWith(IoPath.DirectorySeparatorChar) ? fullPath : fullPath + IoPath.DirectorySeparatorChar;
    }

    private static string NormalizeDirectoryScope(string path)
    {
        var fullPath = IoPath.GetFullPath(path);
        return fullPath.EndsWith(IoPath.DirectorySeparatorChar) ? fullPath : fullPath + IoPath.DirectorySeparatorChar;
    }
}