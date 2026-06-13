namespace VMS_SUSA.Models;

public class AppState
{
    public PrinterConfig PrinterConfig { get; set; } = new();
    public PrinterStatus PrinterStatus { get; set; } = new();
    public string ImportFilePath { get; set; } = string.Empty;
    public DateTime SavedAt { get; set; } = DateTime.Now;
}
