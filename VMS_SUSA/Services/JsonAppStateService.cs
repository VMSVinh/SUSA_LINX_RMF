using System.IO;
using System.Text.Json;
using VMS_SUSA.Models;

namespace VMS_SUSA.Services;

public sealed class JsonAppStateService : IAppStateService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _stateFilePath;

    public JsonAppStateService()
    {
        var rootFolder = Path.Combine(AppContext.BaseDirectory, "Data Logs");
        Directory.CreateDirectory(rootFolder);
        _stateFilePath = Path.Combine(rootFolder, "app_state.json");
    }

    public async Task SaveAsync(AppState state)
    {
        var persistableState = new AppState
        {
            PrinterConfig = state.PrinterConfig ?? new PrinterConfig(),
            PrinterStatus = new PrinterStatus
            {
                IsConnected = state.PrinterStatus?.IsConnected ?? false,
                IsPrinting = state.PrinterStatus?.IsPrinting ?? false,
                PrinterCounter = state.PrinterStatus?.PrinterCounter ?? 0,
                SoftwareCounter = state.PrinterStatus?.SoftwareCounter ?? 0,
                LastSentSerial = state.PrinterStatus?.LastSentSerial ?? string.Empty,
                LastPrintedSerial = state.PrinterStatus?.LastPrintedSerial ?? string.Empty,
                LastError = state.PrinterStatus?.LastError ?? string.Empty,
                LastUpdatedAt = state.PrinterStatus?.LastUpdatedAt ?? DateTime.Now
            },
            SavedAt = DateTime.Now
        };

        var json = JsonSerializer.Serialize(persistableState, JsonOptions);
        await File.WriteAllTextAsync(_stateFilePath, json);
    }

    public async Task<AppState?> LoadAsync()
    {
        if (!File.Exists(_stateFilePath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_stateFilePath);
            var state = JsonSerializer.Deserialize<AppState>(json, JsonOptions);
            if (state?.PrinterStatus is not null)
            {
                state.PrinterStatus.LastReceivedRawData = string.Empty;
                state.PrinterStatus.ReceivedRawDataLog = string.Empty;
            }

            return state;
        }
        catch
        {
            return null;
        }
    }
}
