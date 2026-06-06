namespace VMS_SUSA.Models;

public class AppState
{
    public PrinterConfig PrinterConfig { get; set; } = new();
    public PrinterStatus PrinterStatus { get; set; } = new();
    public List<SerialItem> SerialItems { get; set; } = new();
    public DateTime SavedAt { get; set; } = DateTime.Now;
}
