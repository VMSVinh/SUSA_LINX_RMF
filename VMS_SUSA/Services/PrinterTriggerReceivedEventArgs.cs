namespace VMS_SUSA.Services;

public sealed class PrinterTriggerReceivedEventArgs : EventArgs
{
    public PrinterTriggerReceivedEventArgs(string rawData)
    {
        RawData = rawData;
    }

    public string RawData { get; }
}
