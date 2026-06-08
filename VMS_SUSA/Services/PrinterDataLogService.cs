using System.IO;
using System.Text;
using System.Text.Json;
using VMS_SUSA.Models;

namespace VMS_SUSA.Services;

public sealed class PrinterDataLogService : IPrinterDataLogService
{
    private const int MaxRawLogLines = 1000;

    private sealed class SerialResultsLog
    {
        public DateTime SavedAt { get; set; } = DateTime.Now;
        public List<SerialItem> SerialItems { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _sync = new(1, 1);

    public PrinterDataLogService()
    {
        DataLogsFolderPath = Path.Combine(AppContext.BaseDirectory, "Data Logs");
        Directory.CreateDirectory(DataLogsFolderPath);
        SerialResultsFilePath = Path.Combine(DataLogsFolderPath, "serial_results.json");
        RawPrinterLogFilePath = Path.Combine(DataLogsFolderPath, "printer_raw.log");
    }

    public string DataLogsFolderPath { get; }

    public string SerialResultsFilePath { get; }

    public string RawPrinterLogFilePath { get; }

    public async Task SaveSerialItemsAsync(IEnumerable<SerialItem> items)
    {
        var snapshot = new SerialResultsLog
        {
            SavedAt = DateTime.Now,
            SerialItems = items?.Select(CloneSerialItem).ToList() ?? new List<SerialItem>()
        };

        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            await File.WriteAllTextAsync(SerialResultsFilePath, json, Encoding.UTF8).ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<List<SerialItem>?> LoadSerialItemsAsync()
    {
        if (!File.Exists(SerialResultsFilePath))
        {
            return null;
        }

        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            var json = await File.ReadAllTextAsync(SerialResultsFilePath, Encoding.UTF8).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                var snapshot = JsonSerializer.Deserialize<SerialResultsLog>(json, JsonOptions);
                return snapshot?.SerialItems ?? new List<SerialItem>();
            }
            catch
            {
                return JsonSerializer.Deserialize<List<SerialItem>>(json, JsonOptions);
            }
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

    private static SerialItem CloneSerialItem(SerialItem item)
    {
        return new SerialItem
        {
            Index = item.Index,
            Serial = item.Serial,
            Status = item.Status,
            SentAt = item.SentAt,
            PrintedAt = item.PrintedAt,
            Note = item.Note
        };
    }
}
