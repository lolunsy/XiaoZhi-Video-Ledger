namespace XiaoZhiLedger.Core.Storage;

public static class StoreLocationResolver
{
    public const string DataDirectoryOverrideVariable = "XIAOZHI_LEDGER_DATA_DIR";

    public static string ResolveDataDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable(DataDirectoryOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(overridePath.Trim()));
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XiaoZhiVideoLedger");
    }
}
