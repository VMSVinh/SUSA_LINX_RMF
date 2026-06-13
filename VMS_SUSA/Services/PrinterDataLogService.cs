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
    private const int RawLogFlushDelayMs = 250;

    private readonly SemaphoreSlim _serialSync = new(1, 1);
    private readonly SemaphoreSlim _rawLogSync = new(1, 1);
    private readonly ISerialItemRepository _serialItemRepository;
    private readonly object _rawLogBufferSync = new();
    private readonly List<string> _rawLogBuffer = new();
    private int _rawLogAppendCount;
    private int _rawLogFlushRunning;

    public PrinterDataLogService(ISerialItemRepository serialItemRepository, string? historyDataFolderPath = null)
    {
        _serialItemRepository = serialItemRepository;
        DataLogsFolderPath = Path.Combine(AppContext.BaseDirectory, "Data Logs");
        Directory.CreateDirectory(DataLogsFolderPath);
        SerialResultsFilePath = serialItemRepository.DatabasePath;
        HistoryDataFolderPath = NormalizeHistoryDataFolderPath(historyDataFolderPath);
        RawPrinterLogFilePath = Path.Combine(DataLogsFolderPath, "printer_raw.log");
    }

    public string DataLogsFolderPath { get; }

    public string SerialResultsFilePath { get; }

    public string RawPrinterLogFilePath { get; }

    public string HistoryDataFolderPath { get; }

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

    public Task AppendRawAsync(string rawData)
    {
        if (string.IsNullOrWhiteSpace(rawData))
        {
            return Task.CompletedTask;
        }

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {rawData}";

        lock (_rawLogBufferSync)
        {
            _rawLogBuffer.Add(line);
        }

        if (Interlocked.CompareExchange(ref _rawLogFlushRunning, 1, 0) == 0)
        {
            _ = FlushRawLogBufferAsync();
        }

        return Task.CompletedTask;
    }

    private async Task FlushRawLogBufferAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(RawLogFlushDelayMs).ConfigureAwait(false);

                List<string> batch;
                lock (_rawLogBufferSync)
                {
                    if (_rawLogBuffer.Count == 0)
                    {
                        _rawLogFlushRunning = 0;
                        return;
                    }

                    batch = new List<string>(_rawLogBuffer);
                    _rawLogBuffer.Clear();
                }

                await AppendRawBatchAsync(batch).ConfigureAwait(false);
            }
        }
        finally
        {
            if (Interlocked.Exchange(ref _rawLogFlushRunning, 0) == 1)
            {
                lock (_rawLogBufferSync)
                {
                    if (_rawLogBuffer.Count > 0 && Interlocked.CompareExchange(ref _rawLogFlushRunning, 1, 0) == 0)
                    {
                        _ = FlushRawLogBufferAsync();
                    }
                }
            }
        }
    }

    private async Task AppendRawBatchAsync(IReadOnlyCollection<string> batch)
    {
        if (batch.Count == 0)
        {
            return;
        }

        await _rawLogSync.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(
                RawPrinterLogFilePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite,
                4096,
                FileOptions.Asynchronous);

            await using var writer = new StreamWriter(stream, Encoding.UTF8);
            foreach (var line in batch)
            {
                await writer.WriteLineAsync(line).ConfigureAwait(false);
            }

            await writer.FlushAsync().ConfigureAwait(false);

            _rawLogAppendCount += batch.Count;
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

    private static string NormalizeHistoryDataFolderPath(string? folderPath)
    {
        var path = string.IsNullOrWhiteSpace(folderPath)
            ? Path.Combine(AppContext.BaseDirectory, "History Data")
            : folderPath.Trim();

        if (!Path.IsPathRooted(path))
        {
            path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        }

        Directory.CreateDirectory(path);
        return path;
    }
}
