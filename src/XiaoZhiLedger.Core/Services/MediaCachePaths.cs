using XiaoZhiLedger.Core.Models;

namespace XiaoZhiLedger.Core.Services;

public sealed class MediaCachePaths
{
    public MediaCachePaths(string dataDirectory)
    {
        ThumbnailDirectory = Path.Combine(dataDirectory, "cache", "thumbs");
        ScrubDirectory = Path.Combine(dataDirectory, "cache", "scrub");
        ProxyDirectory = Path.Combine(dataDirectory, "cache", "proxies");
    }

    public string ThumbnailDirectory { get; }
    public string ScrubDirectory { get; }
    public string ProxyDirectory { get; }
    public string GetThumbnail(MediaItem item) => Path.Combine(ThumbnailDirectory, $"{item.Id}_portrait_pair_v1.jpg");
    public string GetScrubSprite(MediaItem item) => Path.Combine(ScrubDirectory, $"{item.Id}_scrub16_v1.jpg");
    public string GetProxy(MediaItem item) => Path.Combine(ProxyDirectory, $"{item.Id}_preview_v2.mp4");
}
