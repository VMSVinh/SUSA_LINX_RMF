using VMS_SUSA.Models;

namespace VMS_SUSA.Services;

public interface IPrinterService
{
    string LastError { get; }
    Task<bool> ConnectAsync(PrinterConfig config);
    Task DisconnectAsync();
    Task<bool> StartPrintAsync();
    Task<bool> StopPrintAsync();
    Task<bool> SendBufferAsync(IEnumerable<SerialItem> items);
    Task<bool> TestRemoteFieldDataAsync(string serial);
    Task<PrinterStatus> GetStatusAsync();
    Task<bool> ResetErrorAsync();
    Task ResetSoftwareCounterAsync();
}

