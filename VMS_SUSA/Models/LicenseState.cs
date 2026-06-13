namespace VMS_SUSA.Models;

public class LicenseState
{
    public DateTime ExpiresAt { get; set; }

    public DateTime LastRunAt { get; set; }

    public string HistoryDataFolderPath { get; set; } = string.Empty;
}
