namespace XiaoZhiLedger.Core.Models;

public sealed record LedgerProjectMaterialState(
    string Status,
    int DragCount,
    string UpdatedAt,
    string LastDraggedAt);
