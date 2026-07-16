namespace XiaoZhiLedger.Core.Models;

public sealed record MediaScanResult(
    string RootPath,
    IReadOnlyList<MediaItem> Items,
    IReadOnlyList<string> Directories,
    IReadOnlyList<string> Warnings,
    TimeSpan Elapsed);

public sealed record MediaScanProgress(int Processed, int Total, string CurrentFile);
