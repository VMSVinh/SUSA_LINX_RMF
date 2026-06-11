using System;
using VMS_SUSA.Models;

namespace VMS_SUSA.Services;

public interface IPrinterService
{
    event EventHandler<PrinterTriggerReceivedEventArgs>? PrintTriggerReceived;

    string LastError { get; }
    Task<bool> ConnectAsync(PrinterConfig config);
    Task DisconnectAsync();
    Task<bool> StartPrintAsync();
    Task<bool> StopPrintAsync();
    Task<bool> SendBufferAsync(IEnumerable<SerialItem> items);
    Task<bool> Send1RemoteFieldDataAsync(string serial);
    Task<bool> ClearDataBufferAsync();
    Task<bool> ResetMessagePrintCountAsync();
    Task<PrinterStatus> GetStatusAsync();
    Task<bool> ResetErrorAsync();
    Task ResetSoftwareCounterAsync();
    Task SetSoftwareCounterAsync(int softwareCounter);
}
