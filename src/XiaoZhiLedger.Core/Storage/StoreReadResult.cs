using XiaoZhiLedger.Core.Models;

namespace XiaoZhiLedger.Core.Storage;

public sealed record StoreReadResult(
    bool IsSuccess,
    LedgerStoreSnapshot? Snapshot,
    string UserMessage,
    Exception? Error = null)
{
    public static StoreReadResult Success(LedgerStoreSnapshot snapshot, string message) =>
        new(true, snapshot, message);

    public static StoreReadResult Failure(string message, Exception error) =>
        new(false, null, message, error);
}
