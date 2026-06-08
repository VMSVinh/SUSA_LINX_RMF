using VMS_SUSA.Models;

namespace VMS_SUSA.Services;

public interface IPrinterDataLogService
{
    string DataLogsFolderPath { get; }
    string SerialResultsFilePath { get; }
    string RawPrinterLogFilePath { get; }

    Task SaveSerialItemsAsync(IEnumerable<SerialItem> items);
    Task<List<SerialItem>?> LoadSerialItemsAsync();
    Task AppendRawAsync(string rawData);
}
