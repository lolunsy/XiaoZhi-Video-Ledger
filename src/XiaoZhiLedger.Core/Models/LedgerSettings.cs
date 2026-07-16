namespace XiaoZhiLedger.Core.Models;

public sealed record LedgerSettings(
    string LegacyRootPath,
    string FfmpegPath,
    string CurrentProjectId,
    double WindowWidth,
    double WindowHeight,
    bool WatchEnabled,
    double CardScale);
