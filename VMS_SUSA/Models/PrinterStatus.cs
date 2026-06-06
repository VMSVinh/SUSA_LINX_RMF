using VMS_SUSA.ViewModels;

namespace VMS_SUSA.Models;

public class PrinterStatus : ViewModelBase
{
    private bool _isConnected;
    private bool _isPrinting;
    private int _printerCounter;
    private int _softwareCounter;
    private int _bufferCount;
    private string _lastSentSerial = string.Empty;
    private string _lastPrintedSerial = string.Empty;
    private string _lastError = string.Empty;
    private DateTime _lastUpdatedAt = DateTime.Now;

    public bool IsConnected
    {
        get => _isConnected;
        set => SetProperty(ref _isConnected, value);
    }

    public bool IsPrinting
    {
        get => _isPrinting;
        set => SetProperty(ref _isPrinting, value);
    }

    public int PrinterCounter
    {
        get => _printerCounter;
        set => SetProperty(ref _printerCounter, value);
    }

    public int SoftwareCounter
    {
        get => _softwareCounter;
        set => SetProperty(ref _softwareCounter, value);
    }

    public int BufferCount
    {
        get => _bufferCount;
        set => SetProperty(ref _bufferCount, value);
    }

    public string LastSentSerial
    {
        get => _lastSentSerial;
        set => SetProperty(ref _lastSentSerial, value);
    }

    public string LastPrintedSerial
    {
        get => _lastPrintedSerial;
        set => SetProperty(ref _lastPrintedSerial, value);
    }

    public string LastError
    {
        get => _lastError;
        set => SetProperty(ref _lastError, value);
    }

    public DateTime LastUpdatedAt
    {
        get => _lastUpdatedAt;
        set => SetProperty(ref _lastUpdatedAt, value);
    }
}
