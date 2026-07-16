namespace XiaoZhiLedger.Core.Storage;

public sealed class MigrationBackupService
{
    private const string BackupPattern = "store-before-csharp-migration-*.json";
    private readonly string _dataDirectory;
    private readonly TimeProvider _timeProvider;

    public MigrationBackupService(string dataDirectory, TimeProvider? timeProvider = null)
    {
        _dataDirectory = dataDirectory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public MigrationBackupResult EnsureBackup()
    {
        var storePath = Path.Combine(_dataDirectory, "store.json");
        if (!File.Exists(storePath))
        {
            return MigrationBackupResult.NotRequired("没有发现既有账本，无需创建迁移备份。");
        }

        try
        {
            var backupDirectory = Path.Combine(_dataDirectory, "backups");
            Directory.CreateDirectory(backupDirectory);

            var existing = Directory.GetFiles(backupDirectory, BackupPattern)
                .OrderByDescending(File.GetCreationTimeUtc)
                .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (existing.Count > 0)
            {
                TrimOldBackups(existing);
                return MigrationBackupResult.AlreadyExists(existing[0], "迁移前备份已经存在。");
            }

            var timestamp = _timeProvider.GetLocalNow().ToString("yyyyMMdd-HHmmss-fff");
            var backupPath = Path.Combine(
                backupDirectory,
                $"store-before-csharp-migration-{timestamp}.json");

            File.Copy(storePath, backupPath, overwrite: false);
            TrimOldBackups(Directory.GetFiles(backupDirectory, BackupPattern)
                .OrderByDescending(File.GetCreationTimeUtc)
                .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList());

            return MigrationBackupResult.Created(backupPath, "已创建迁移前只读备份。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return MigrationBackupResult.Failure(
                "无法创建迁移前备份。为保护现有数据，本次没有继续读取账本。",
                error);
        }
    }

    private static void TrimOldBackups(IReadOnlyList<string> backups)
    {
        foreach (var obsoletePath in backups.Skip(10))
        {
            File.Delete(obsoletePath);
        }
    }
}

public sealed record MigrationBackupResult(
    bool IsSuccess,
    bool WasRequired,
    bool WasCreated,
    string? BackupPath,
    string UserMessage,
    Exception? Error = null)
{
    public static MigrationBackupResult NotRequired(string message) =>
        new(true, false, false, null, message);

    public static MigrationBackupResult AlreadyExists(string path, string message) =>
        new(true, true, false, path, message);

    public static MigrationBackupResult Created(string path, string message) =>
        new(true, true, true, path, message);

    public static MigrationBackupResult Failure(string message, Exception error) =>
        new(false, true, false, null, message, error);
}
