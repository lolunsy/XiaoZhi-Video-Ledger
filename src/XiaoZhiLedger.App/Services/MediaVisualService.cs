using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XiaoZhiLedger.App.ViewModels;
using XiaoZhiLedger.Core.Models;
using XiaoZhiLedger.Core.Services;

namespace XiaoZhiLedger.App.Services;

public sealed class MediaVisualService : IDisposable
{
    private readonly FfmpegService _ffmpeg;
    private readonly MediaCachePaths _cachePaths;
    private readonly string _appDirectory;
    private readonly SemaphoreSlim _thumbnailGate = new(4, 4);
    private readonly SemaphoreSlim _cacheReadGate = new(8, 8);
    private readonly SemaphoreSlim _scrubGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _thumbnailBatchSync = new();
    private CancellationTokenSource? _thumbnailBatch;
    private FfmpegToolset? _tools;

    public MediaVisualService(
        FfmpegService ffmpeg,
        MediaCachePaths cachePaths,
        string appDirectory)
    {
        _ffmpeg = ffmpeg;
        _cachePaths = cachePaths;
        _appDirectory = appDirectory;
    }

    public bool IsAvailable => _tools is not null;
    public string StatusText => _tools is null ? "FFmpeg：未设置" : "FFmpeg：已就绪";
    public string? ResolvedFfmpegPath => _tools?.FfmpegPath;

    public void Configure(string configuredPath)
    {
        CancelThumbnailBatch();
        _tools = _ffmpeg.ResolveToolset(_appDirectory, configuredPath);
    }

    public void QueueThumbnails(IEnumerable<MediaCardViewModel> cards)
    {
        var visibleCards = cards.ToList();
        var tasks = new ConcurrentDictionary<string, Task<BitmapImage?>>(StringComparer.OrdinalIgnoreCase);
        CancellationTokenSource batch;
        CancellationTokenSource? previous;
        lock (_thumbnailBatchSync)
        {
            if (_shutdown.IsCancellationRequested)
            {
                return;
            }

            previous = _thumbnailBatch;
            batch = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            _thumbnailBatch = batch;
        }

        previous?.Cancel();
        previous?.Dispose();

        foreach (var card in visibleCards)
        {
            card.SetPreviewBusy(true, "正在准备代表帧…");
            _ = ApplyThumbnailAsync(card, tasks, batch.Token);
        }
    }

    public void CancelThumbnailBatch()
    {
        CancellationTokenSource? batch;
        lock (_thumbnailBatchSync)
        {
            batch = _thumbnailBatch;
            _thumbnailBatch = null;
        }

        batch?.Cancel();
        batch?.Dispose();
    }

    public bool HasScrubCache(MediaCardViewModel card)
    {
        var path = _cachePaths.GetScrubSprite(card.Item);
        return File.Exists(path) && new FileInfo(path).Length > 0;
    }

    public async Task<bool> EnsureScrubAsync(MediaCardViewModel card, CancellationToken cancellationToken)
    {
        if (HasScrubCache(card))
        {
            return true;
        }

        // Interactive scrubbing has priority over background representative-frame work.
        CancelThumbnailBatch();
        await _scrubGate.WaitAsync(cancellationToken);
        try
        {
            if (HasScrubCache(card))
            {
                return true;
            }

            var tools = _tools;
            if (tools is null)
            {
                return false;
            }

            var path = _cachePaths.GetScrubSprite(card.Item);
            await _ffmpeg.GenerateScrubSpriteAsync(tools, card.Item, path, cancellationToken);
            return HasScrubCache(card);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
        finally
        {
            _scrubGate.Release();
        }
    }

    public void ShowScrubFrame(MediaCardViewModel card, double ratio)
    {
        var path = _cachePaths.GetScrubSprite(card.Item);
        if (!File.Exists(path))
        {
            return;
        }

        var sprite = LoadBitmap(path);
        var tileWidth = sprite.PixelWidth / 4;
        var tileHeight = sprite.PixelHeight / 4;
        var index = Math.Clamp((int)Math.Floor(Math.Clamp(ratio, 0, 1) * 16), 0, 15);
        var source = new Int32Rect((index % 4) * tileWidth, (index / 4) * tileHeight, tileWidth, tileHeight);
        var cropped = new CroppedBitmap(sprite, source);
        cropped.Freeze();
        card.SetScrubPreview(cropped, ratio);
    }

    public void RestoreThumbnail(MediaCardViewModel card) => card.RestoreBasePreview();

    public async Task<string> GetProxyAsync(MediaCardViewModel card, CancellationToken cancellationToken = default)
    {
        var output = _cachePaths.GetProxy(card.Item);
        if (File.Exists(output))
        {
            return output;
        }

        if (_tools is null)
        {
            return card.Path;
        }

        await _ffmpeg.GenerateProxyAsync(_tools, card.Item, output, cancellationToken);
        return output;
    }

    private async Task ApplyThumbnailAsync(
        MediaCardViewModel card,
        ConcurrentDictionary<string, Task<BitmapImage?>> tasks,
        CancellationToken cancellationToken)
    {
        try
        {
            var task = tasks.GetOrAdd(card.Id, _ => LoadOrGenerateThumbnailAsync(card.Item, cancellationToken));
            var image = await task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (image is not null)
            {
                card.SetBasePreview(image);
            }
            else
            {
                card.SetPreviewUnavailable("需要设置 FFmpeg");
            }

            card.SetPreviewBusy(false, "");
        }
        catch (OperationCanceledException)
        {
            // A newer filter, folder, project, or scan owns the visible card queue now.
        }
        catch
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                card.SetPreviewUnavailable("代表帧生成失败");
                card.SetPreviewBusy(false, "");
            }
        }
    }

    private async Task<BitmapImage?> LoadOrGenerateThumbnailAsync(
        MediaItem item,
        CancellationToken cancellationToken)
    {
        var path = _cachePaths.GetThumbnail(item);
        var cached = await TryLoadBitmapAsync(path, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        var tools = _tools;
        if (tools is null)
        {
            return null;
        }

        await _thumbnailGate.WaitAsync(cancellationToken);
        try
        {
            // Another card with identical content may have filled the cache while this one waited.
            cached = await TryLoadBitmapAsync(path, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                return cached;
            }

            await _ffmpeg.GenerateThumbnailPairAsync(tools, item, path, cancellationToken).ConfigureAwait(false);
            return LoadBitmap(path);
        }
        finally
        {
            _thumbnailGate.Release();
        }
    }

    private static bool TryLoadBitmap(string path, out BitmapImage? bitmap)
    {
        bitmap = null;
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            return false;
        }

        try
        {
            bitmap = LoadBitmap(path);
            return true;
        }
        catch
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // A corrupt cache can be retried after another process releases it.
            }

            return false;
        }
    }

    private async Task<BitmapImage?> TryLoadBitmapAsync(string path, CancellationToken cancellationToken)
    {
        await _cacheReadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return TryLoadBitmap(path, out var bitmap) ? bitmap : null;
        }
        finally
        {
            _cacheReadGate.Release();
        }
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public void Dispose()
    {
        CancelThumbnailBatch();
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
