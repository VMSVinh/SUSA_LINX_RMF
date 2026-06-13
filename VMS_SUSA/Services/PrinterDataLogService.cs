using System.IO;
using System.Text;
using System.Text.Json;
using VMS_SUSA.Models;
using VMS_SUSA.Repositories;

namespace VMS_SUSA.Services;

public sealed class PrinterDataLogService : IPrinterDataLogService
{
    private const int MaxRawLogLines = 1000;
    private const int RawLogTrimInterval = 200;

    private readonly SemaphoreSlim _serialSync = new(1, 1);
    private readonly SemaphoreSlim _rawLogSync = new(1, 1);
    private readonly ISerialItemRepository _serialItemRepository;
    private int _rawLogAppendCount;

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
        await _serialSync.WaitAsync().ConfigureAwait(false);
        try
        {
            await _serialItemRepository.ReplaceAllAsync(items ?? Enumerable.Empty<SerialItem>()).ConfigureAwait(false);
        }
        finally
        {
            _serialSync.Release();
        }
    }

    public async Task<List<SerialItem>?> LoadSerialItemsAsync()
    {
        await _serialSync.WaitAsync().ConfigureAwait(false);
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
            _serialSync.Release();
        }
    }

    public async Task AppendRawAsync(string rawData)
    {
        if (string.IsNullOrWhiteSpace(rawData))
        {
            return;
        }

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {rawData}";

        await _rawLogSync.WaitAsync().ConfigureAwait(false);
        try
        {
            await using (var stream = new FileStream(
                             RawPrinterLogFilePath,
                             FileMode.Append,
                             FileAccess.Write,
                             FileShare.ReadWrite,
                             4096,
                             FileOptions.Asynchronous))
            {
                await using var writer = new StreamWriter(stream, Encoding.UTF8);
                await writer.WriteLineAsync(line).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }

            _rawLogAppendCount++;
            if (_rawLogAppendCount >= RawLogTrimInterval)
            {
                _rawLogAppendCount = 0;
                await TrimRawLogIfNeededAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _rawLogSync.Release();
        }
    }

    private async Task TrimRawLogIfNeededAsync()
    {
        if (!File.Exists(RawPrinterLogFilePath))
        {
            return;
        }

        var lines = (await File.ReadAllLinesAsync(RawPrinterLogFilePath, Encoding.UTF8).ConfigureAwait(false))
            .Where(existingLine => !string.IsNullOrWhiteSpace(existingLine))
            .ToList();

        if (lines.Count <= MaxRawLogLines)
        {
            return;
        }

        var trimmedLines = lines.Skip(lines.Count - MaxRawLogLines).ToArray();
        await File.WriteAllLinesAsync(RawPrinterLogFilePath, trimmedLines, Encoding.UTF8).ConfigureAwait(false);
    }
}
