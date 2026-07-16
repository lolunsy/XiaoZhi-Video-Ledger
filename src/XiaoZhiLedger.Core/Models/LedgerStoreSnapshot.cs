namespace XiaoZhiLedger.Core.Models;

public sealed record LedgerStoreSnapshot(
    string SourcePath,
    bool SourceExists,
    LedgerSettings Settings,
    IReadOnlyList<LedgerProject> Projects,
    IReadOnlyDictionary<string, LedgerRecordSnapshot> Records)
{
    public int RecordCount => Records.Count;

    public LedgerProject? CurrentProject =>
        Projects.FirstOrDefault(project =>
            string.Equals(project.Id, Settings.CurrentProjectId, StringComparison.Ordinal))
        ?? Projects.FirstOrDefault();
}
