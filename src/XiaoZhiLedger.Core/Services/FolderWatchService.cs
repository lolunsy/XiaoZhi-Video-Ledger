namespace XiaoZhiLedger.Core.Services;

public sealed class FolderWatchService : IDisposable
{
    private readonly object _sync = new();
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _debounce;
    private Task? _signatureLoop;
    private string _rootPath = "";
    private ulong _lastSignature;

    public event EventHandler<FolderRefreshRequestedEventArgs>? RefreshRequested;

    public bool IsRunning => _watcher is not null;

    public void Start(string rootPath)
    {
        Stop();
        if (!Directory.Exists(rootPath))
        {
            return;
        }

        _rootPath = Path.GetFullPath(rootPath);
        _lastSignature = ComputeSignature(_rootPath);
        _lifetime = new CancellationTokenSource();
        _watcher = new FileSystemWatcher(_rootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                           NotifyFilters.Size | NotifyFilters.LastWrite,
            Filter = "*",
            InternalBufferSize = 64 * 1024,
            EnableRaisingEvents = true
        };
        _watcher.Created += OnChanged;
        _watcher.Changed += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
        _signatureLoop = RunSignatureLoopAsync(_lifetime.Token);
    }

    public void Stop()
    {
        lock (_sync)
        {
            _debounce?.Cancel();
            _debounce?.Dispose();
            _debounce = null;
        }

        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnChanged;
            _watcher.Changed -= OnChanged;
            _watcher.Deleted -= OnChanged;
            _watcher.Renamed -= OnRenamed;
            _watcher.Error -= OnError;
            _watcher.Dispose();
            _watcher = null;
        }

        _signatureLoop = null;
        _rootPath = "";
    }

    private void OnChanged(object sender, FileSystemEventArgs eventArgs)
    {
        if (Directory.Exists(eventArgs.FullPath)
            || MediaScanner.SupportedExtensions.Contains(Path.GetExtension(eventArgs.FullPath)))
        {
            ScheduleRefresh("检测到素材目录变化");
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs eventArgs)
    {
        if (Directory.Exists(eventArgs.FullPath)
            || MediaScanner.SupportedExtensions.Contains(Path.GetExtension(eventArgs.FullPath))
            || MediaScanner.SupportedExtensions.Contains(Path.GetExtension(eventArgs.OldFullPath)))
        {
            ScheduleRefresh("检测到素材移动或重命名");
        }
    }

    private void OnError(object sender, ErrorEventArgs eventArgs) =>
        ScheduleRefresh("目录监控已恢复并核对素材");

    private void ScheduleRefresh(string reason)
    {
        CancellationToken token;
        lock (_sync)
        {
            _debounce?.Cancel();
            _debounce?.Dispose();
            _debounce = CancellationTokenSource.CreateLinkedTokenSource(_lifetime?.Token ?? CancellationToken.None);
            token = _debounce.Token;
        }

        _ = DebounceAsync(reason, token);
    }

    private async Task DebounceAsync(string reason, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            RefreshRequested?.Invoke(this, new FolderRefreshRequestedEventArgs(reason));
        }
        catch (OperationCanceledException)
        {
            // A later file event restarted the quiet period.
        }
    }

    private async Task RunSignatureLoopAsync(CancellationToken cancellationToken)
    {
        var interval = _rootPath.StartsWith("\\\\", StringComparison.Ordinal)
            ? TimeSpan.FromSeconds(60)
            : TimeSpan.FromSeconds(30);
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!Directory.Exists(_rootPath))
                {
                    RefreshRequested?.Invoke(this, new FolderRefreshRequestedEventArgs("素材路径暂时不可访问"));
                    continue;
                }

                var signature = ComputeSignature(_rootPath);
                if (signature != _lastSignature)
                {
                    _lastSignature = signature;
                    RefreshRequested?.Invoke(this, new FolderRefreshRequestedEventArgs("定时核对发现素材变化"));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    internal static ulong ComputeSignature(string rootPath)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
            };
            foreach (var path in Directory.EnumerateFiles(rootPath, "*", options)
                         .Where(path => MediaScanner.SupportedExtensions.Contains(Path.GetExtension(path)))
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var file = new FileInfo(path);
                var value = $"{Path.GetRelativePath(rootPath, path).ToLowerInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
                foreach (var character in value)
                {
                    hash ^= character;
                    hash *= prime;
                }
            }
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }

        return hash;
    }

    public void Dispose() => Stop();
}

public sealed record FolderRefreshRequestedEventArgs(string Reason);
