using System.IO;
using System.Text;
using System.Text.Json;
using VMS_SUSA.Models;
using VMS_SUSA.Repositories;

namespace VMS_SUSA.Services;

public sealed class PrinterDataLogService : IPrinterDataLogService
{
    private const int MaxRawLogLines = 1000;

    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly ISerialItemRepository _serialItemRepository;

    public PrinterDataLogService(ISerialItemRepository serialItemRepository)
    {
        _serialItemRepository = serialItemRepository;
        DataLogsFolderPath = Path.Combine(AppContext.BaseDirectory, "Data Logs");
        Directory.CreateDirectory(DataLogsFolderPath);
        SerialResultsFilePath = serialItemRepository.DatabasePath;
        RawPrinterLogFilePath = Path.Combine(DataLogsFolderPath, "printer_raw.log");
    }

    public string DataLogsFolderPath { get; }

    public string SerialResultsFilePath { get; }

    public string RawPrinterLogFilePath { get; }

    public async Task SaveSerialItemsAsync(IEnumerable<SerialItem> items)
    {
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            await _serialItemRepository.ReplaceAllAsync(items ?? Enumerable.Empty<SerialItem>()).ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<List<SerialItem>?> LoadSerialItemsAsync()
    {
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            var items = await _serialItemRepository.GetAllAsync().ConfigureAwait(false);
            return items.Count == 0 ? null : items;
        }
        catch
        {
            return null;
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task AppendRawAsync(string rawData)
    {
        if (string.IsNullOrWhiteSpace(rawData))
        {
            return;
        }

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {rawData}";

        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            var lines = new List<string>();
            if (File.Exists(RawPrinterLogFilePath))
            {
                lines = (await File.ReadAllLinesAsync(RawPrinterLogFilePath, Encoding.UTF8).ConfigureAwait(false))
                    .Where(existingLine => !string.IsNullOrWhiteSpace(existingLine))
                    .TakeLast(MaxRawLogLines - 1)
                    .ToList();
            }

            lines.Add(line);
            await File.WriteAllLinesAsync(RawPrinterLogFilePath, lines, Encoding.UTF8).ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }
    }
}
