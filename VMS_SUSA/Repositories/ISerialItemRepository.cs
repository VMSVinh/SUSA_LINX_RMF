using VMS_SUSA.Models;

namespace VMS_SUSA.Repositories;

public interface ISerialItemRepository
{
    string DatabasePath { get; }

    Task InitializeAsync();
    Task ReplaceAllAsync(IEnumerable<SerialItem> items, IProgress<int>? progress = null);
    Task<List<SerialItem>> GetAllAsync();
    Task<List<SerialItem>> GetFilteredAsync(string? searchText = null, SerialStatus? statusFilter = null);
    IAsyncEnumerable<SerialItem> StreamAllAsync();
    IAsyncEnumerable<SerialItem> StreamByStatusesAsync(IEnumerable<SerialStatus> statuses);
    Task<SerialItemPageResult> GetPageAsync(int pageNumber, int pageSize, string? searchText = null, SerialStatus? statusFilter = null);
    Task<SerialItemStatistics> GetStatisticsAsync();
    Task<SerialItem?> GetFirstByStatusAsync(SerialStatus status);
    Task<SerialItem?> GetLastByStatusAsync(SerialStatus status);
    Task<List<SerialItem>> GetByStatusesAsync(IEnumerable<SerialStatus> statuses);
    Task<int> CountSendableAfterIndexAsync(int displayIndexExclusive);
    Task<List<SerialItem>> GetSendableAfterIndexAsync(int displayIndexExclusive, int take);
    Task<string?> ReconcilePrintedItemsAsync(int printerCounter);
    Task RecalculateDuplicateStatusesAsync(int serialLength);
    Task UpdateAsync(SerialItem item);
    Task UpdateRangeAsync(IEnumerable<SerialItem> items);
    Task DeleteAllAsync();
}
