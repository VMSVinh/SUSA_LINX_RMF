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
        var rootFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicPrinterApp");
        Directory.CreateDirectory(rootFolder);
        _stateFilePath = Path.Combine(rootFolder, "app_state.json");
    }

    public async Task SaveAsync(AppState state)
    {
        state.SavedAt = DateTime.Now;
        var json = JsonSerializer.Serialize(state, JsonOptions);
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
            return JsonSerializer.Deserialize<AppState>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
