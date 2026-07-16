namespace XiaoZhiLedger.Core.Models;

public sealed record MediaItem(
    string Id,
    string Path,
    string Name,
    string Extension,
    string Folder,
    string RelativeFolder,
    string RelativePath,
    long Size,
    DateTime LastWriteTime,
    DateTime LastWriteTimeUtc);
