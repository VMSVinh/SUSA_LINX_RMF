using VMS_SUSA.Models;

namespace VMS_SUSA.Repositories;

public sealed record SerialItemPageResult(
    IReadOnlyList<SerialItem> Items,
    int TotalCount,
    int PageNumber,
    int PageSize)
{
    public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
}

public sealed record SerialItemStatistics(
    int TotalCount,
    int SentCount,
    int PrintedCount,
    int ErrorCount,
    int DuplicateCount,
    int InvalidCount);
