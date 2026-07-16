using System.Diagnostics;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using XiaoZhiLedger.App.ViewModels;
using XiaoZhiLedger.App.Views;
using XiaoZhiLedger.App.Services;
using XiaoZhiLedger.Core.Services;
using XiaoZhiLedger.Core.Storage;

namespace XiaoZhiLedger.App;

public partial class MainWindow : Window
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private readonly MainWindowViewModel _viewModel;
    private readonly Dictionary<string, CancellationTokenSource> _scrubHoverTokens = new(StringComparer.OrdinalIgnoreCase);
    private Point? _dragStart;
    private MediaCardViewModel? _dragCard;
    private DependencyObject? _dragSource;
    private double _savedScrollOffset;
    private HwndSource? _windowSource;

    public MainWindow()
    {
        InitializeComponent();
        UpdateWindowAppearance();
        SourceInitialized += MainWindow_SourceInitialized;
        var dataDirectory = StoreLocationResolver.ResolveDataDirectory();
        var repository = new LedgerStoreRepository(dataDirectory);
        var visualService = new MediaVisualService(
            new FfmpegService(),
            new MediaCachePaths(dataDirectory),
            AppContext.BaseDirectory);
        _viewModel = new MainWindowViewModel(
            new StoreService(dataDirectory),
            new MigrationBackupService(dataDirectory),
            new MediaScanner(new MediaFingerprintService()),
            repository,
            visualService,
            new FolderWatchService());
        DataContext = _viewModel;
        _viewModel.MediaRefreshStarting += (_, _) => _savedScrollOffset = CardsScrollViewer.VerticalOffset;
        _viewModel.MediaRefreshCompleted += (_, _) =>
            Dispatcher.BeginInvoke(() => CardsScrollViewer.ScrollToVerticalOffset(_savedScrollOffset));
        Loaded += MainWindow_Loaded;
        Closed += (_, _) =>
        {
            foreach (var token in _scrubHoverTokens.Values)
            {
                token.Cancel();
                token.Dispose();
            }
            _scrubHoverTokens.Clear();
            _windowSource?.RemoveHook(WindowMessageHook);
            _viewModel.Dispose();
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        await _viewModel.LoadAsync();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        SourceInitialized -= MainWindow_SourceInitialized;
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(WindowMessageHook);
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmGetMinMaxInfo)
        {
            ConstrainMaximizedWindowToWorkArea(hwnd, lParam);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static void ConstrainMaximizedWindowToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        var minMax = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        minMax.MaxPosition.X = monitorInfo.WorkArea.Left - monitorInfo.MonitorArea.Left;
        minMax.MaxPosition.Y = monitorInfo.WorkArea.Top - monitorInfo.MonitorArea.Top;
        minMax.MaxSize.X = monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left;
        minMax.MaxSize.Y = monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top;
        minMax.MaxTrackSize = minMax.MaxSize;
        Marshal.StructureToPtr(minMax, lParam, false);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择包含原始视频及子文件夹的素材根目录",
            Multiselect = false
        };
        if (Directory.Exists(_viewModel.PathInput))
        {
            dialog.InitialDirectory = _viewModel.PathInput;
        }

        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.LoadSelectedPathAsync(dialog.FolderName);
        }
    }

    private void PathInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel.LoadPathCommand.CanExecute(null))
        {
            _viewModel.LoadPathCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNodeViewModel folder)
        {
            _viewModel.SelectedFolder = folder;
        }
    }

    private void CardsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        var options = _viewModel.CardScaleOptions;
        var currentIndex = Enumerable.Range(0, options.Count)
            .First(index => options[index] == _viewModel.SelectedCardScale);
        var nextIndex = Math.Clamp(currentIndex + (e.Delta > 0 ? 1 : -1), 0, options.Count - 1);
        _viewModel.SelectedCardScale = options[nextIndex];
        e.Handled = true;
    }

    private async void NewProjectButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TextInputDialog(
            "新建剪辑项目",
            "输入项目名称。新项目会使用独立的素材路径、状态和使用次数。",
            "新剪辑项目") { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            await _viewModel.CreateProjectAsync(dialog.Value);
        }
    }

    private async void RenameProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.CurrentProject is null)
        {
            return;
        }

        var dialog = new TextInputDialog(
            "重命名项目",
            "修改当前剪辑项目名称。",
            _viewModel.CurrentProject.DisplayName) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            await _viewModel.RenameCurrentProjectAsync(dialog.Value);
        }
    }

    private async void DeleteProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.CurrentProject is null)
        {
            return;
        }

        if (_viewModel.Projects.Count <= 1)
        {
            MessageBox.Show(this, "至少需要保留一个剪辑项目。", "无法删除项目",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var summary = _viewModel.GetCurrentProjectDeleteSummary();
        var answer = MessageBox.Show(this,
            $"确定删除项目「{_viewModel.CurrentProject.DisplayName}」吗？\n\n" +
            $"已记录状态的素材：{summary.StateCount} 条\n累计使用次数：{summary.DragCount} 次\n\n" +
            "只会删除该项目的路径、状态和次数，不会删除、移动或修改任何原视频。\n此操作不能撤销。",
            "删除剪辑项目", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
        {
            await _viewModel.DeleteCurrentProjectAsync();
        }
    }

    private async void ResetStateButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "选择“是”重置当前项目；选择“否”重置全部项目；选择“取消”不操作。\n\n" +
            "会清除状态和使用次数，保留项目、路径、备注、原视频和缓存。此操作不能撤销。",
            "重置状态", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.Yes)
        {
            await _viewModel.ResetStatesAsync(allProjects: false);
        }
        else if (answer == MessageBoxResult.No)
        {
            await _viewModel.ResetStatesAsync(allProjects: true);
        }
    }

    private void StatusButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not MediaCardViewModel card)
        {
            return;
        }

        var menu = new ContextMenu();
        AddStatusItem(menu, card, "未使用", "unused");
        AddStatusItem(menu, card, "已使用", "used");
        AddStatusItem(menu, card, "备选", "candidate");
        AddStatusItem(menu, card, "不考虑", "excluded");
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void AddStatusItem(ContextMenu menu, MediaCardViewModel card, string label, string status)
    {
        var item = new MenuItem { Header = label, IsChecked = card.Status == status };
        item.Click += async (_, _) => await _viewModel.SetStatusAsync(card, status);
        menu.Items.Add(item);
    }

    private async void NoteBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: MediaCardViewModel card })
        {
            await _viewModel.SaveNoteAsync(card);
        }
    }

    private async void FfmpegButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 ffmpeg.exe",
            Filter = "ffmpeg.exe|ffmpeg.exe|可执行文件|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.SetFfmpegPathAsync(dialog.FileName);
        }
    }

    private async void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MediaCardViewModel card })
        {
            await OpenPreviewAsync(card);
        }
    }

    private async Task OpenPreviewAsync(MediaCardViewModel card)
    {
        try
        {
            var path = await _viewModel.GetPreviewPathAsync(card);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"无法打开预览：\n{error.Message}", "预览失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LocateButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MediaCardViewModel card } || !File.Exists(card.Path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{card.Path}\"")
        {
            UseShellExecute = true
        });
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MediaCardViewModel card } || !File.Exists(card.Path))
        {
            return;
        }

        Clipboard.SetFileDropList(new StringCollection { card.Path });
        _viewModel.NotifyClipboardReady(card);
    }

    private async void PreviewHost_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border { DataContext: MediaCardViewModel card } border)
        {
            return;
        }

        CancelScrubHover(card.Id);
        var cancellation = new CancellationTokenSource();
        _scrubHoverTokens[card.Id] = cancellation;
        try
        {
            await Task.Delay(350, cancellation.Token);
            if (border.IsMouseOver && await _viewModel.EnsureScrubAsync(card, cancellation.Token))
            {
                _viewModel.ShowScrubFrame(card, 0.5);
            }
        }
        catch (OperationCanceledException)
        {
            // Passing over a card should not queue work.
        }
    }

    private async void PreviewHost_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Border { DataContext: MediaCardViewModel card } border)
        {
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed && await TryStartDragAsync(border, card, e.GetPosition(this)))
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Released
            || border.ActualWidth <= 1)
        {
            return;
        }

        var ratio = Math.Clamp(e.GetPosition(border).X / border.ActualWidth, 0, 1);
        _viewModel.ShowScrubFrame(card, ratio);
    }

    private void PreviewHost_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border { DataContext: MediaCardViewModel card })
        {
            CancelScrubHover(card.Id);
            _viewModel.EndScrub(card);
        }
    }

    private async void PreviewHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { DataContext: MediaCardViewModel card } border)
        {
            BeginDrag(border, card, e.GetPosition(this));
            if (e.ClickCount == 2)
            {
                ClearDrag();
                e.Handled = true;
                await OpenPreviewAsync(card);
            }
        }
    }

    private void DragButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button { DataContext: MediaCardViewModel card } button)
        {
            BeginDrag(button, card, e.GetPosition(this));
        }
    }

    private async void DragButton_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed
            && sender is Button { DataContext: MediaCardViewModel card } button)
        {
            await TryStartDragAsync(button, card, e.GetPosition(this));
        }
    }

    private void DragSource_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => ClearDrag();

    private void BeginDrag(DependencyObject source, MediaCardViewModel card, Point position)
    {
        _dragStart = position;
        _dragCard = card;
        _dragSource = source;
    }

    private async Task<bool> TryStartDragAsync(
        DependencyObject source,
        MediaCardViewModel card,
        Point currentPosition)
    {
        if (_dragStart is null || _dragCard != card || _dragSource != source || !File.Exists(card.Path))
        {
            return false;
        }

        var delta = currentPosition - _dragStart.Value;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return false;
        }

        CancelScrubHover(card.Id);
        ClearDrag();
        var data = new DataObject(DataFormats.FileDrop, new[] { card.Path });
        var effect = DragDrop.DoDragDrop(source, data, DragDropEffects.Copy);
        if ((effect & DragDropEffects.Copy) != 0)
        {
            await _viewModel.RecordSuccessfulDragAsync(card);
        }
        else
        {
            _viewModel.NotifyDragCancelled(card);
        }

        return true;
    }

    private void ClearDrag()
    {
        _dragStart = null;
        _dragCard = null;
        _dragSource = null;
    }

    private void CancelScrubHover(string itemId)
    {
        if (_scrubHoverTokens.Remove(itemId, out var token))
        {
            token.Cancel();
            token.Dispose();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        SystemCommands.MinimizeWindow(this);

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        SystemCommands.CloseWindow(this);

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        UpdateWindowAppearance();
    }

    private void UpdateWindowAppearance()
    {
        if (MaximizeButton is null || MaximizeIconPath is null || WindowRoot is null ||
            WindowFrame is null || WindowShadow is null || WindowKeyShadow is null || MainWindowChrome is null)
        {
            return;
        }

        var isMaximized = WindowState == WindowState.Maximized;
        WindowRoot.Margin = isMaximized ? new Thickness(0) : new Thickness(16);
        WindowFrame.CornerRadius = isMaximized ? new CornerRadius(0) : new CornerRadius(10);
        WindowShadow.Visibility = isMaximized ? Visibility.Collapsed : Visibility.Visible;
        WindowKeyShadow.Visibility = isMaximized ? Visibility.Collapsed : Visibility.Visible;
        MainWindowChrome.CaptionHeight = isMaximized ? 52 : 68;
        UpdateWindowFrameClip(isMaximized);
        MaximizeIconPath.Data = (System.Windows.Media.Geometry)FindResource(
            isMaximized ? "RestoreIconGeometry" : "MaximizeIconGeometry");
        MaximizeButton.SetValue(AutomationProperties.NameProperty,
            isMaximized ? "还原" : "最大化");
    }

    private void WindowFrame_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateWindowFrameClip(WindowState == WindowState.Maximized);

    private void UpdateWindowFrameClip(bool isMaximized)
    {
        if (WindowFrame is null)
        {
            return;
        }

        if (isMaximized)
        {
            WindowFrame.Clip = null;
            return;
        }

        var width = WindowFrame.ActualWidth;
        var height = WindowFrame.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        WindowFrame.Clip = new System.Windows.Media.RectangleGeometry(
            new Rect(0, 0, width, height), 10, 10);
    }
}
