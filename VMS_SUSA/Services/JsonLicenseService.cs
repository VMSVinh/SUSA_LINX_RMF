using System.IO;
using System.Text.Json;
using VMS_SUSA.Models;

namespace VMS_SUSA.Services;

public sealed class JsonLicenseService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _licenseFilePath;

    public JsonLicenseService()
    {
        var rootFolder = Path.Combine(AppContext.BaseDirectory, "Data Logs");
        Directory.CreateDirectory(rootFolder);
        _licenseFilePath = Path.Combine(rootFolder, "license.json");
    }

    public string LicenseFilePath => _licenseFilePath;

    public async Task<LicenseState?> LoadAsync()
    {
        if (!File.Exists(_licenseFilePath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_licenseFilePath);
            return JsonSerializer.Deserialize<LicenseState>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveAsync(LicenseState license)
    {
        var persistable = new LicenseState
        {
            ExpiresAt = license.ExpiresAt,
            LastRunAt = license.LastRunAt
        };

        var json = JsonSerializer.Serialize(persistable, JsonOptions);
        await File.WriteAllTextAsync(_licenseFilePath, json);
    }
}
