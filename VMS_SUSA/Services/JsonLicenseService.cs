using System.IO;
using System.Text.Json;
using VMS_SUSA.Models;

namespace VMS_SUSA.Services;

public sealed class JsonLicenseService
{
    private static readonly string DefaultHistoryDataFolderPath = Path.Combine(AppContext.BaseDirectory, "History Data");
    private static readonly string DefaultDataLogsFolderName = "Data Logs";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _licenseFilePath;

    public JsonLicenseService()
    {
        var rootFolder = ResolveDataLogsFolder();
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
            var state = JsonSerializer.Deserialize<LicenseState>(json, JsonOptions);
            if (state is not null)
            {
                state.HistoryDataFolderPath = NormalizeHistoryDataFolderPath(state.HistoryDataFolderPath);
            }

            return state;
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
            LastRunAt = license.LastRunAt,
            HistoryDataFolderPath = NormalizeHistoryDataFolderPath(license.HistoryDataFolderPath)
        };

        var json = JsonSerializer.Serialize(persistable, JsonOptions);
        await File.WriteAllTextAsync(_licenseFilePath, json);
    }

    private static string NormalizeHistoryDataFolderPath(string? folderPath)
    {
        var path = string.IsNullOrWhiteSpace(folderPath)
            ? DefaultHistoryDataFolderPath
            : folderPath.Trim();

        if (!Path.IsPathRooted(path))
        {
            path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        }

        Directory.CreateDirectory(path);
        return path;
    }

    private static string ResolveDataLogsFolder()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, DefaultDataLogsFolderName);
            if (File.Exists(Path.Combine(candidate, "license.json")))
            {
                return candidate;
            }

            current = current.Parent;
        }

        var currentDirectoryCandidate = Path.Combine(Directory.GetCurrentDirectory(), DefaultDataLogsFolderName);
        if (File.Exists(Path.Combine(currentDirectoryCandidate, "license.json")))
        {
            return currentDirectoryCandidate;
        }

        return Path.Combine(AppContext.BaseDirectory, DefaultDataLogsFolderName);
    }
}
