namespace XiaoZhiLedger.Core;

/// <summary>
/// Identifies the compatibility baseline for the staged C# migration.
/// No user data is read or written in Milestone 1.
/// </summary>
public static class ProductBaseline
{
    public const string StableVersion = "WinForms v0.1.2";
    public const string LatestReferenceVersion = "WinForms v0.1.4";
    public const int StoreCompatibilityVersion = 1;
}
