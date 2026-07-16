namespace XiaoZhiLedger.Core.Models;

public sealed record LedgerRecordSnapshot(
    string Id,
    string Path,
    string Name,
    long Size,
    string LastSeen,
    string Note,
    IReadOnlyDictionary<string, LedgerProjectMaterialState> Projects)
{
    public LedgerProjectMaterialState GetProjectState(string projectId) =>
        Projects.TryGetValue(projectId, out var state)
            ? state
            : new LedgerProjectMaterialState("unused", 0, "", "");
}
