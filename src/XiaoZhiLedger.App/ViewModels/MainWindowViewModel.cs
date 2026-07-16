using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using XiaoZhiLedger.Core.Models;
using XiaoZhiLedger.Core.Services;
using XiaoZhiLedger.Core.Storage;
using XiaoZhiLedger.App.Services;

namespace XiaoZhiLedger.App.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private const int PageSize = 60;
    private readonly StoreService _storeService;
    private readonly MigrationBackupService _backupService;
    private readonly MediaScanner _mediaScanner;
    private readonly LedgerStoreRepository _repository;
    private readonly MediaVisualService _visualService;
    private readonly FolderWatchService _folderWatchService;
    private readonly SynchronizationContext _uiContext;
    private readonly List<MediaCardViewModel> _allMedia = [];
    private readonly List<UndoEntry> _undoStack = [];
    private LedgerStoreSnapshot? _snapshot;
    private MediaScanResult? _lastScanResult;
    private CancellationTokenSource? _scanCancellation;
    private LedgerProject? _currentProject;
    private FolderNodeViewModel? _selectedFolder;
    private bool _suppressProjectScan;
    private bool _canWrite;
    private bool _hasProjects;
    private bool _hasMedia;
    private bool _hasMoreMedia;
    private bool _isScanning;
    private bool _includeChildren;
    private bool _watchEnabled;
    private bool _isLoaded;
    private bool _watchRefreshPending;
    private int _visibleLimit = PageSize;
    private int _totalCount;
    private int _unusedCount;
    private int _usedCount;
    private string _pathInput = "";
    private string _searchText = "";
    private string _headerModeText = "Milestone 3 · 正在加载";
    private string _dataBannerTitle = "正在检查现有账本";
    private string _dataBannerDetail = "准备项目和媒体扫描服务。";
    private string _statusText = "正在准备数据层…";
    private string _backupStatusText = "迁移备份：检查中";
    private string _historyText = "历史记录：0";
    private string _emptyStateTitle = "正在连接项目数据";
    private string _emptyStateDetail = "请稍候。";
    private string _summaryText = "尚未扫描素材";
    private string _viewCountText = "显示 0 条";
    private string _undoText = "撤销  0";
    private string _ffmpegStatusText = "FFmpeg：检测中";
    private string _watchStatusText = "监控：准备中";
    private SelectionOption _selectedStatusFilter;
    private SelectionOption _selectedSortOption;
    private CardScaleOption _selectedCardScale;

    public MainWindowViewModel(
        StoreService storeService,
        MigrationBackupService backupService,
        MediaScanner mediaScanner,
        LedgerStoreRepository repository,
        MediaVisualService visualService,
        FolderWatchService folderWatchService)
    {
        _storeService = storeService;
        _backupService = backupService;
        _mediaScanner = mediaScanner;
        _repository = repository;
        _visualService = visualService;
        _folderWatchService = folderWatchService;
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
        _folderWatchService.RefreshRequested += FolderWatchService_RefreshRequested;
        _selectedStatusFilter = StatusFilters[0];
        _selectedSortOption = SortOptions[0];
        _selectedCardScale = CardScaleOptions.First(option => Math.Abs(option.Value - 1.0) < 0.001);
        RescanCommand = new AsyncRelayCommand(RescanAsync, () => !IsScanning && CurrentProject is not null);
        LoadPathCommand = new AsyncRelayCommand(() => ScanPathAsync(PathInput), () => !IsScanning);
        LoadMoreCommand = new RelayCommand(LoadMore, () => HasMoreMedia);
        UndoCommand = new AsyncRelayCommand(UndoAsync, () => _undoStack.Count > 0 && CanWrite);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? MediaRefreshStarting;
    public event EventHandler? MediaRefreshCompleted;

    public ObservableCollection<LedgerProject> Projects { get; } = [];
    public ObservableCollection<FolderNodeViewModel> FolderRoots { get; } = [];
    public ObservableCollection<MediaCardViewModel> VisibleMedia { get; } = [];
    public IReadOnlyList<SelectionOption> StatusFilters { get; } =
    [
        new("all", "全部状态"), new("unused", "未使用"), new("used", "已使用"),
        new("candidate", "备选"), new("excluded", "不考虑")
    ];
    public IReadOnlyList<SelectionOption> SortOptions { get; } =
    [
        new("dateDesc", "修改时间 ↓"), new("dateAsc", "修改时间 ↑"),
        new("name", "文件名"), new("sizeDesc", "文件大小 ↓"), new("unusedFirst", "未使用优先")
    ];
    public IReadOnlyList<CardScaleOption> CardScaleOptions { get; } =
    [
        new(0.7, "极简 70%"), new(0.8, "紧凑 80%"), new(1.0, "标准 100%"),
        new(1.2, "较大 120%"), new(1.4, "特大 140%")
    ];

    public AsyncRelayCommand RescanCommand { get; }
    public AsyncRelayCommand LoadPathCommand { get; }
    public RelayCommand LoadMoreCommand { get; }
    public AsyncRelayCommand UndoCommand { get; }

    public LedgerProject? CurrentProject
    {
        get => _currentProject;
        set
        {
            if (!SetField(ref _currentProject, value))
            {
                return;
            }

            PathInput = value?.RootPath ?? "";
            OnPropertyChanged(nameof(CurrentRootPath));
            OnPropertyChanged(nameof(CurrentRootPathHint));
            OnPropertyChanged(nameof(CurrentProjectName));
            RescanCommand.RaiseCanExecuteChanged();
            if (!_suppressProjectScan)
            {
                _ = SwitchProjectAsync();
            }
        }
    }

    public FolderNodeViewModel? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (SetField(ref _selectedFolder, value))
            {
                _visibleLimit = PageSize;
                ApplyFilters();
                if (!_suppressProjectScan && value is not null)
                {
                    _ = PersistSelectedFolderAsync(value.FullPath);
                }
            }
        }
    }

    public bool HasProjects { get => _hasProjects; private set => SetField(ref _hasProjects, value); }
    public bool CanWrite { get => _canWrite; private set => SetField(ref _canWrite, value); }
    public bool HasMedia { get => _hasMedia; private set => SetField(ref _hasMedia, value); }
    public bool HasMoreMedia
    {
        get => _hasMoreMedia;
        private set
        {
            if (SetField(ref _hasMoreMedia, value))
            {
                LoadMoreCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetField(ref _isScanning, value))
            {
                RescanCommand.RaiseCanExecuteChanged();
                LoadPathCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public bool IncludeChildren
    {
        get => _includeChildren;
        set
        {
            if (SetField(ref _includeChildren, value))
            {
                _visibleLimit = PageSize;
                ApplyFilters();
            }
        }
    }
    public bool WatchEnabled
    {
        get => _watchEnabled;
        set
        {
            if (!SetField(ref _watchEnabled, value))
            {
                return;
            }

            ConfigureFolderWatcher();
            if (_isLoaded && CanWrite)
            {
                _ = PersistWatchEnabledAsync(value);
            }
        }
    }
    public string PathInput { get => _pathInput; set => SetField(ref _pathInput, value); }
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetField(ref _searchText, value))
            {
                _visibleLimit = PageSize;
                ApplyFilters();
            }
        }
    }
    public SelectionOption SelectedStatusFilter
    {
        get => _selectedStatusFilter;
        set
        {
            if (value is not null && SetField(ref _selectedStatusFilter, value))
            {
                _visibleLimit = PageSize;
                ApplyFilters();
            }
        }
    }
    public SelectionOption SelectedSortOption
    {
        get => _selectedSortOption;
        set
        {
            if (value is not null && SetField(ref _selectedSortOption, value))
            {
                ApplyFilters();
            }
        }
    }
    public CardScaleOption SelectedCardScale
    {
        get => _selectedCardScale;
        set
        {
            if (value is not null && SetField(ref _selectedCardScale, value))
            {
                OnPropertyChanged(nameof(CardWidth));
                OnPropertyChanged(nameof(PreviewHeight));
                if (CanWrite)
                {
                    _ = PersistCardScaleAsync(value.Value);
                }
            }
        }
    }
    public double CardWidth => 310 * SelectedCardScale.Value;
    public double PreviewHeight => 270 * SelectedCardScale.Value;
    public int TotalCount { get => _totalCount; private set => SetField(ref _totalCount, value); }
    public int UnusedCount { get => _unusedCount; private set => SetField(ref _unusedCount, value); }
    public int UsedCount { get => _usedCount; private set => SetField(ref _usedCount, value); }
    public string HeaderModeText { get => _headerModeText; private set => SetField(ref _headerModeText, value); }
    public string DataBannerTitle { get => _dataBannerTitle; private set => SetField(ref _dataBannerTitle, value); }
    public string DataBannerDetail { get => _dataBannerDetail; private set => SetField(ref _dataBannerDetail, value); }
    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }
    public string BackupStatusText { get => _backupStatusText; private set => SetField(ref _backupStatusText, value); }
    public string HistoryText { get => _historyText; private set => SetField(ref _historyText, value); }
    public string EmptyStateTitle { get => _emptyStateTitle; private set => SetField(ref _emptyStateTitle, value); }
    public string EmptyStateDetail { get => _emptyStateDetail; private set => SetField(ref _emptyStateDetail, value); }
    public string SummaryText { get => _summaryText; private set => SetField(ref _summaryText, value); }
    public string ViewCountText { get => _viewCountText; private set => SetField(ref _viewCountText, value); }
    public string UndoText { get => _undoText; private set => SetField(ref _undoText, value); }
    public string FfmpegStatusText { get => _ffmpegStatusText; private set => SetField(ref _ffmpegStatusText, value); }
    public string WatchStatusText { get => _watchStatusText; private set => SetField(ref _watchStatusText, value); }
    public string CurrentProjectName => CurrentProject?.DisplayName ?? "未选择项目";
    public string CurrentRootPath => CurrentProject?.DisplayRootPath ?? "账本尚未加载";
    public string CurrentRootPathHint => string.IsNullOrWhiteSpace(CurrentProject?.RootPath)
        ? "当前项目尚未设置素材路径"
        : "当前项目的素材根目录";

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var backup = _backupService.EnsureBackup();
        BackupStatusText = !backup.IsSuccess ? "迁移备份：失败"
            : !backup.WasRequired ? "迁移备份：无需创建"
            : backup.WasCreated ? "迁移备份：已创建" : "迁移备份：已存在";
        if (!backup.IsSuccess)
        {
            ShowFailure(backup.UserMessage);
            return;
        }

        var result = await _storeService.LoadReadOnlyAsync(cancellationToken);
        if (!result.IsSuccess || result.Snapshot is null)
        {
            ShowFailure(result.UserMessage);
            return;
        }

        var createdFreshStore = false;
        if (!result.Snapshot.SourceExists)
        {
            try
            {
                createdFreshStore = await _repository.CreateInitialStoreAsync("默认项目", cancellationToken);
                result = await _storeService.LoadReadOnlyAsync(cancellationToken);
                if (!result.IsSuccess || result.Snapshot is null || !result.Snapshot.SourceExists)
                {
                    ShowFailure("首次启动账本创建失败，请检查本机数据目录的写入权限。");
                    return;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ShowFailure($"首次启动账本创建失败：{error.Message}");
                return;
            }
        }

        _snapshot = result.Snapshot;
        _visualService.Configure(_snapshot.Settings.FfmpegPath);
        FfmpegStatusText = _visualService.StatusText;
        _watchEnabled = _snapshot.Settings.WatchEnabled;
        OnPropertyChanged(nameof(WatchEnabled));
        if (_snapshot.SourceExists)
        {
            try
            {
                await _repository.InitializeAsync(cancellationToken);
                CanWrite = true;
            }
            catch (Exception error)
            {
                ShowFailure($"账本写入保护初始化失败：{error.Message}");
                return;
            }
        }
        Projects.Clear();
        foreach (var project in _snapshot.Projects)
        {
            Projects.Add(project);
        }

        HasProjects = Projects.Count > 0;
        _suppressProjectScan = true;
        CurrentProject = Projects.FirstOrDefault(project =>
            string.Equals(project.Id, _snapshot.Settings.CurrentProjectId, StringComparison.Ordinal))
            ?? Projects.FirstOrDefault();
        _suppressProjectScan = false;
        SelectedCardScale = CardScaleOptions.First(option =>
            Math.Abs(option.Value - _snapshot.Settings.CardScale) < 0.001);
        HeaderModeText = "C# 重构版 · 本地账本";
        HistoryText = $"{Projects.Count} 个项目 · 历史记录：{_snapshot.RecordCount}";
        await ScanCurrentProjectAsync();
        if (createdFreshStore)
        {
            DataBannerTitle = "欢迎使用小智剪辑分类账";
            DataBannerDetail = "首次启动账本已经创建；请在左侧浏览或粘贴你的素材目录。";
            StatusText = "首次设置：选择素材目录后即可开始扫描和分类。";
        }
        _isLoaded = true;
        ConfigureFolderWatcher();
    }

    public Task LoadSelectedPathAsync(string path) => ScanPathAsync(path);

    public async Task SetFfmpegPathAsync(string path)
    {
        if (!File.Exists(path))
        {
            StatusText = "选择的 ffmpeg.exe 不存在。";
            return;
        }

        if (CanWrite)
        {
            await _repository.SetFfmpegPathAsync(path);
        }

        _visualService.Configure(path);
        FfmpegStatusText = _visualService.StatusText;
        QueueVisibleThumbnails();
        StatusText = _visualService.IsAvailable ? "FFmpeg 已设置并可用。" : "没有找到可用的 FFmpeg。";
    }

    public async Task<bool> EnsureScrubAsync(MediaCardViewModel card, CancellationToken cancellationToken)
    {
        var wasCached = _visualService.HasScrubCache(card);
        try
        {
            return await _visualService.EnsureScrubAsync(card, cancellationToken);
        }
        finally
        {
            if (!wasCached)
            {
                QueueVisibleThumbnails();
            }
        }
    }

    public void ShowScrubFrame(MediaCardViewModel card, double ratio) =>
        _visualService.ShowScrubFrame(card, ratio);

    public void EndScrub(MediaCardViewModel card) => _visualService.RestoreThumbnail(card);

    public Task<string> GetPreviewPathAsync(MediaCardViewModel card, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StatusText = $"正在用系统播放器打开原片：{card.Name}";
        return Task.FromResult(card.Path);
    }

    public async Task CreateProjectAsync(string name)
    {
        if (!CanWrite || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var project = await _repository.CreateProjectAsync(name);
        Projects.Add(project);
        HasProjects = true;
        _suppressProjectScan = true;
        CurrentProject = project;
        _suppressProjectScan = false;
        ClearUndo();
        await ScanCurrentProjectAsync();
        StatusText = $"项目「{project.DisplayName}」已创建，请设置独立素材路径。";
    }

    public async Task RenameCurrentProjectAsync(string name)
    {
        if (!CanWrite || CurrentProject is null || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await _repository.RenameProjectAsync(CurrentProject.Id, name);
        _suppressProjectScan = true;
        CurrentProject = CurrentProject with { Name = name.Trim() };
        _suppressProjectScan = false;
        ReplaceCurrentProjectInList();
        StatusText = "项目名称已保存。";
    }

    public (int StateCount, int DragCount) GetCurrentProjectDeleteSummary()
    {
        if (_snapshot is null || CurrentProject is null)
        {
            return (0, 0);
        }

        var states = _snapshot.Records.Values
            .Where(record => record.Projects.ContainsKey(CurrentProject.Id))
            .Select(record => record.Projects[CurrentProject.Id])
            .ToList();
        return (states.Count, states.Sum(state => state.DragCount));
    }

    public async Task DeleteCurrentProjectAsync()
    {
        if (!CanWrite || CurrentProject is null || Projects.Count <= 1)
        {
            return;
        }

        var currentIndex = Projects.IndexOf(CurrentProject);
        var nextIndex = currentIndex >= Projects.Count - 1 ? currentIndex - 1 : currentIndex + 1;
        var next = Projects[nextIndex];
        var deleted = CurrentProject;
        await _repository.DeleteProjectAsync(deleted.Id, next.Id);
        Projects.Remove(deleted);
        _suppressProjectScan = true;
        CurrentProject = next;
        _suppressProjectScan = false;
        ClearUndo();
        await RefreshSnapshotAsync();
        await ScanCurrentProjectAsync();
        StatusText = $"项目「{deleted.DisplayName}」已删除，原视频未受影响。";
    }

    public async Task ResetStatesAsync(bool allProjects)
    {
        if (!CanWrite || CurrentProject is null)
        {
            return;
        }

        await _repository.ResetStatesAsync(allProjects ? null : CurrentProject.Id);
        ClearUndo();
        await RefreshSnapshotAsync();
        await ScanCurrentProjectAsync();
        StatusText = allProjects ? "全部项目的状态和次数已重置。" : "当前项目的状态和次数已重置。";
    }

    public async Task SetStatusAsync(MediaCardViewModel card, string status)
    {
        if (!CanWrite || CurrentProject is null || card.Status == status)
        {
            return;
        }

        var oldState = card.CaptureState();
        await _repository.SetProjectStateAsync(card.Id, CurrentProject.Id, status, incrementDragCount: false);
        PushUndo(new UndoEntry(card.Id, CurrentProject.Id, oldState));
        card.ApplyState(new LedgerProjectMaterialState(
            status,
            card.DragCount,
            DateTimeOffset.Now.ToString("o"),
            oldState?.LastDraggedAt ?? ""));
        RefreshAfterCardChange();
        StatusText = $"已将「{card.Name}」标记为{StatusLabel(status)}。";
    }

    public async Task SaveNoteAsync(MediaCardViewModel card)
    {
        if (!CanWrite)
        {
            return;
        }

        await _repository.SetNoteAsync(card.Id, card.Note ?? "");
        StatusText = $"已保存「{card.Name}」的备注。";
    }

    public async Task RecordSuccessfulDragAsync(MediaCardViewModel card)
    {
        if (!CanWrite || CurrentProject is null)
        {
            return;
        }

        var oldState = card.CaptureState();
        await _repository.SetProjectStateAsync(card.Id, CurrentProject.Id, "used", incrementDragCount: true);
        PushUndo(new UndoEntry(card.Id, CurrentProject.Id, oldState));
        card.ApplySuccessfulDrag();
        RefreshAfterCardChange();
        StatusText = $"剪映已接受素材；使用次数已更新为 {card.DragCount}。";
    }

    public void NotifyClipboardReady(MediaCardViewModel card) =>
        StatusText = $"已复制「{card.Name}」；可在剪映中按 Ctrl+V。复制本身不计入使用次数。";

    public void NotifyDragCancelled(MediaCardViewModel card) =>
        StatusText = $"目标未接受「{card.Name}」，使用次数未变化。";

    private async Task UndoAsync()
    {
        if (!CanWrite || _undoStack.Count == 0)
        {
            return;
        }

        var entry = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        await _repository.RestoreProjectStateAsync(entry.ItemId, entry.ProjectId, entry.OldState);
        var card = _allMedia.FirstOrDefault(item => item.Id == entry.ItemId);
        card?.ApplyState(entry.OldState);
        UpdateUndoState();
        RefreshAfterCardChange();
        StatusText = $"已撤销一步，还可撤销 {_undoStack.Count} 步。";
    }

    private async Task SwitchProjectAsync()
    {
        if (CurrentProject is null)
        {
            return;
        }

        if (CanWrite)
        {
            await _repository.SetCurrentProjectAsync(CurrentProject.Id);
        }

        ClearUndo();
        await RefreshSnapshotAsync();
        await ScanCurrentProjectAsync();
    }

    private async Task PersistSelectedFolderAsync(string folderPath)
    {
        if (!CanWrite || CurrentProject is null)
        {
            return;
        }

        await _repository.UpdateProjectWorkspaceAsync(CurrentProject.Id, CurrentProject.RootPath, folderPath);
        _suppressProjectScan = true;
        CurrentProject = CurrentProject with { SelectedFolder = folderPath };
        _suppressProjectScan = false;
        ReplaceCurrentProjectInList();
    }

    private async Task PersistCardScaleAsync(double scale)
    {
        try
        {
            await _repository.SetCardScaleAsync(scale);
        }
        catch (Exception error)
        {
            StatusText = $"卡片比例保存失败：{error.Message}";
        }
    }

    private async Task PersistWatchEnabledAsync(bool enabled)
    {
        try
        {
            await _repository.SetWatchEnabledAsync(enabled);
            WatchStatusText = enabled ? "监控：已开启" : "监控：已关闭";
        }
        catch (Exception error)
        {
            WatchStatusText = "监控：保存失败";
            StatusText = $"自动监控设置保存失败：{error.Message}";
        }
    }

    private void ConfigureFolderWatcher()
    {
        _folderWatchService.Stop();
        if (!WatchEnabled || CurrentProject is null || !Directory.Exists(CurrentProject.RootPath))
        {
            WatchStatusText = WatchEnabled ? "监控：等待可用路径" : "监控：已关闭";
            return;
        }

        _folderWatchService.Start(CurrentProject.RootPath);
        WatchStatusText = "监控：已开启";
    }

    private void FolderWatchService_RefreshRequested(object? sender, FolderRefreshRequestedEventArgs eventArgs)
    {
        _uiContext.Post(_ => _ = RefreshFromWatchAsync(eventArgs.Reason), null);
    }

    private async Task RefreshFromWatchAsync(string reason)
    {
        if (!WatchEnabled)
        {
            return;
        }

        if (IsScanning)
        {
            _watchRefreshPending = true;
            return;
        }

        MediaRefreshStarting?.Invoke(this, EventArgs.Empty);
        StatusText = $"{reason}，正在后台刷新…";
        try
        {
            await ScanCurrentProjectAsync();
        }
        finally
        {
            MediaRefreshCompleted?.Invoke(this, EventArgs.Empty);
        }

        if (_watchRefreshPending)
        {
            _watchRefreshPending = false;
            await RefreshFromWatchAsync("合并刷新期间收到的后续变化");
        }
    }

    private async Task RefreshSnapshotAsync()
    {
        var result = await _storeService.LoadReadOnlyAsync();
        if (result.IsSuccess && result.Snapshot is not null)
        {
            _snapshot = result.Snapshot;
            HistoryText = $"{Projects.Count} 个项目 · 历史记录：{_snapshot.RecordCount}";
        }
    }

    private void ReplaceCurrentProjectInList()
    {
        var current = CurrentProject;
        if (current is null)
        {
            return;
        }

        var index = Projects.ToList().FindIndex(project => project.Id == current.Id);
        if (index >= 0)
        {
            _suppressProjectScan = true;
            Projects[index] = current;
            _currentProject = current;
            OnPropertyChanged(nameof(CurrentProject));
            _suppressProjectScan = false;
        }
    }

    private void RefreshAfterCardChange()
    {
        RefreshFolderCounts();
        ApplyFilters();
        UpdateSummary();
    }

    private void RefreshFolderCounts()
    {
        if (_lastScanResult is null || FolderRoots.Count == 0)
        {
            return;
        }

        var tree = FolderTreeBuilder.Build(
            _lastScanResult.RootPath,
            _lastScanResult.Directories,
            _lastScanResult.Items,
            item => _allMedia.First(card => card.Id == item.Id).Status);
        FolderRoots[0].UpdateCounts(tree);
    }

    private void RebuildFolderTree()
    {
        if (_lastScanResult is null)
        {
            return;
        }

        var selectedPath = SelectedFolder?.FullPath;
        var tree = FolderTreeBuilder.Build(
            _lastScanResult.RootPath,
            _lastScanResult.Directories,
            _lastScanResult.Items,
            item => _allMedia.First(card => card.Id == item.Id).Status);
        var root = new FolderNodeViewModel(tree);
        FolderRoots.Clear();
        FolderRoots.Add(root);
        _suppressProjectScan = true;
        SelectedFolder = FindFolder(root, selectedPath) ?? root;
        _suppressProjectScan = false;
    }

    private void PushUndo(UndoEntry entry)
    {
        while (_undoStack.Count >= 50)
        {
            _undoStack.RemoveAt(0);
        }

        _undoStack.Add(entry);
        UpdateUndoState();
    }

    private void ClearUndo()
    {
        _undoStack.Clear();
        UpdateUndoState();
    }

    private void UpdateUndoState()
    {
        UndoText = $"撤销  {_undoStack.Count}";
        UndoCommand.RaiseCanExecuteChanged();
    }

    private static string StatusLabel(string status) => status switch
    {
        "used" => "已使用",
        "candidate" => "备选",
        "excluded" => "不考虑",
        _ => "未使用"
    };

    private async Task ScanCurrentProjectAsync()
    {
        if (CurrentProject is null || string.IsNullOrWhiteSpace(CurrentProject.RootPath))
        {
            ClearMaterials("当前项目尚未设置素材路径", "输入或浏览素材目录后即可开始扫描。", "当前项目路径为空。");
            return;
        }

        await ScanPathAsync(CurrentProject.RootPath);
    }

    private Task RescanAsync() => ScanPathAsync(PathInput);

    private async Task ScanPathAsync(string path)
    {
        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        _scanCancellation = new CancellationTokenSource();
        var token = _scanCancellation.Token;
        PathInput = MediaScanner.NormalizeInputPath(path);
        IsScanning = true;
        DataBannerTitle = "正在扫描素材目录";
        DataBannerDetail = "后台读取文件信息和兼容指纹；不会修改视频。";
        StatusText = "正在枚举视频文件…";
        var progress = new Progress<MediaScanProgress>(value =>
        {
            if (value.Processed == value.Total || value.Processed % 8 == 0)
            {
                StatusText = $"正在扫描 {value.Processed}/{value.Total}：{value.CurrentFile}";
            }
        });

        try
        {
            var result = await _mediaScanner.ScanAsync(PathInput, progress, token);
            if (CanWrite && CurrentProject is not null)
            {
                var selectedFolder = !string.IsNullOrWhiteSpace(CurrentProject.SelectedFolder)
                                     && result.Directories.Contains(CurrentProject.SelectedFolder, StringComparer.OrdinalIgnoreCase)
                    ? CurrentProject.SelectedFolder
                    : result.RootPath;
                await _repository.EnsureRecordsAsync(result.Items, token);
                await _repository.UpdateProjectWorkspaceAsync(CurrentProject.Id, result.RootPath, selectedFolder, token);
                _suppressProjectScan = true;
                CurrentProject = CurrentProject with { RootPath = result.RootPath, SelectedFolder = selectedFolder };
                _suppressProjectScan = false;
                ReplaceCurrentProjectInList();
                var refreshed = await _storeService.LoadReadOnlyAsync(token);
                if (refreshed.IsSuccess && refreshed.Snapshot is not null)
                {
                    _snapshot = refreshed.Snapshot;
                }
            }

            _lastScanResult = result;
            _allMedia.Clear();
            foreach (var item in result.Items)
            {
                var record = _snapshot?.Records.GetValueOrDefault(item.Id);
                var state = record?.GetProjectState(CurrentProject?.Id ?? "")
                    ?? new LedgerProjectMaterialState("unused", 0, "", "");
                var hadState = record?.Projects.ContainsKey(CurrentProject?.Id ?? "") == true;
                _allMedia.Add(new MediaCardViewModel(item, state, record?.Note ?? "", hadState));
            }

            var tree = FolderTreeBuilder.Build(
                result.RootPath,
                result.Directories,
                result.Items,
                item => _allMedia.First(card => card.Id == item.Id).Status);
            FolderRoots.Clear();
            var rootViewModel = new FolderNodeViewModel(tree);
            FolderRoots.Add(rootViewModel);
            _suppressProjectScan = true;
            SelectedFolder = FindFolder(rootViewModel, CurrentProject?.SelectedFolder) ?? rootViewModel;
            _suppressProjectScan = false;
            _visibleLimit = PageSize;
            ApplyFilters();
            UpdateSummary();
            HeaderModeText = "C# 重构版 · 数据已保护";
            DataBannerTitle = "素材扫描完成";
            DataBannerDetail = $"找到 {_allMedia.Count} 条视频和 {result.Directories.Count} 个目录；兼容记录已原子保存。";
            StatusText = result.Warnings.Count == 0
                ? $"扫描完成：{_allMedia.Count} 条视频，用时 {result.Elapsed.TotalSeconds:N1} 秒。"
                : $"扫描完成：{_allMedia.Count} 条视频，跳过 {result.Warnings.Count} 个无法读取的文件。";
            EmptyStateTitle = _allMedia.Count == 0 ? "这个目录中没有支持的视频" : "当前筛选没有匹配素材";
            EmptyStateDetail = "支持 MP4、MOV、MKV、AVI、M4V、WebM、MTS 等常用格式。";
            ConfigureFolderWatcher();
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消上一次扫描。";
        }
        catch (MediaScanException error)
        {
            ClearMaterials("无法访问素材路径", error.Message, error.Message);
            HeaderModeText = "C# 重构版 · 路径不可用";
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void ApplyFilters()
    {
        IEnumerable<MediaCardViewModel> query = _allMedia;
        if (SelectedFolder is not null)
        {
            var folder = SelectedFolder.FullPath;
            var prefix = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
            query = query.Where(card => string.Equals(card.Item.Folder, folder, StringComparison.OrdinalIgnoreCase)
                || (IncludeChildren && card.Item.Folder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
        }

        if (SelectedStatusFilter.Value != "all")
        {
            query = query.Where(card => card.Status == SelectedStatusFilter.Value);
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var term = SearchText.Trim();
            query = query.Where(card => card.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                || card.Item.RelativePath.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                || card.Note.Contains(term, StringComparison.CurrentCultureIgnoreCase));
        }

        query = SelectedSortOption.Value switch
        {
            "dateAsc" => query.OrderBy(card => card.Item.LastWriteTime),
            "name" => query.OrderBy(card => card.Name, StringComparer.CurrentCultureIgnoreCase),
            "sizeDesc" => query.OrderByDescending(card => card.Item.Size),
            "unusedFirst" => query.OrderBy(card => card.Status == "unused" ? 0 : 1)
                .ThenByDescending(card => card.Item.LastWriteTime),
            _ => query.OrderByDescending(card => card.Item.LastWriteTime)
        };

        var filtered = query.ToList();
        VisibleMedia.Clear();
        foreach (var card in filtered.Take(_visibleLimit))
        {
            VisibleMedia.Add(card);
        }

        HasMedia = VisibleMedia.Count > 0;
        HasMoreMedia = filtered.Count > VisibleMedia.Count;
        ViewCountText = HasMoreMedia
            ? $"显示 {VisibleMedia.Count} / {filtered.Count} 条"
            : $"显示 {filtered.Count} 条";
        QueueVisibleThumbnails();
    }

    private void LoadMore()
    {
        _visibleLimit += PageSize;
        ApplyFilters();
    }

    private void UpdateSummary()
    {
        var used = _allMedia.Count(card => card.Status == "used");
        var candidate = _allMedia.Count(card => card.Status == "candidate");
        var excluded = _allMedia.Count(card => card.Status == "excluded");
        var unused = _allMedia.Count - used - candidate - excluded;
        TotalCount = _allMedia.Count;
        UnusedCount = unused;
        UsedCount = used;
        SummaryText = $"全部 {_allMedia.Count}  ·  未使用 {unused}  ·  已使用 {used}  ·  备选 {candidate}  ·  不考虑 {excluded}";
    }

    private void ClearMaterials(string title, string detail, string status)
    {
        _visualService.CancelThumbnailBatch();
        _allMedia.Clear();
        VisibleMedia.Clear();
        FolderRoots.Clear();
        SelectedFolder = null;
        HasMedia = false;
        HasMoreMedia = false;
        TotalCount = 0;
        UnusedCount = 0;
        UsedCount = 0;
        SummaryText = "尚未扫描素材";
        ViewCountText = "显示 0 条";
        EmptyStateTitle = title;
        EmptyStateDetail = detail;
        DataBannerTitle = title;
        DataBannerDetail = detail;
        StatusText = status;
    }

    private void ShowFailure(string message)
    {
        Projects.Clear();
        HasProjects = false;
        CurrentProject = null;
        HeaderModeText = "数据受保护";
        ClearMaterials("现有数据保持原样", "请继续使用稳定版；新版未执行任何写入。", message);
    }

    private static FolderNodeViewModel? FindFolder(FolderNodeViewModel root, string? path)
    {
        if (string.Equals(root.FullPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        foreach (var child in root.Children)
        {
            var match = FindFolder(child, path);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void QueueVisibleThumbnails()
    {
        _visualService.QueueThumbnails(VisibleMedia);
    }

    public void Dispose()
    {
        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        _folderWatchService.RefreshRequested -= FolderWatchService_RefreshRequested;
        _folderWatchService.Dispose();
        _visualService.Dispose();
    }
}

internal sealed record UndoEntry(
    string ItemId,
    string ProjectId,
    LedgerProjectMaterialState? OldState);
