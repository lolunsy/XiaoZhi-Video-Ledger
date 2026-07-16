namespace XiaoZhiLedger.Core.Models;

public sealed class FolderNodeModel
{
    public FolderNodeModel(string name, string fullPath)
    {
        Name = name;
        FullPath = fullPath;
    }

    public string Name { get; }
    public string FullPath { get; }
    public List<FolderNodeModel> Children { get; } = [];
    public int TotalCount { get; internal set; }
    public int UnusedCount { get; internal set; }
    public string DisplayText => $"{Name}   未用 {UnusedCount} / 总 {TotalCount}";
}
