namespace XiaoZhiLedger.Core.Models;

public sealed record LedgerProject(
    string Id,
    string Name,
    string RootPath,
    string SelectedFolder,
    string CreatedAt)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "未命名项目" : Name;

    public string DisplayRootPath => string.IsNullOrWhiteSpace(RootPath)
        ? "尚未设置素材路径"
        : RootPath;
}
