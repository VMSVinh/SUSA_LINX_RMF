using VMS_SUSA.ViewModels;

namespace VMS_SUSA.Models;

public class PrinterConfig : ViewModelBase
{
    private string _printerName = "Máy in công nghiệp";
    private string _modelName = string.Empty;
    private string _ipAddress = "192.168.1.100";
    private int _port = 9100;
    private int _timeoutMs = 3000;
    private int _serialLength = 20;
    private bool _checksumEnabled;
    private bool _metricModeEnabled;
    private bool _extendedStatus;
    private string _licenseKey = string.Empty;

    public string PrinterName
    {
        get => _printerName;
        set => SetProperty(ref _printerName, value);
    }

    public string ModelName
    {
        get => _modelName;
        set => SetProperty(ref _modelName, value);
    }

    public string IpAddress
    {
        get => _ipAddress;
        set => SetProperty(ref _ipAddress, value);
    }

    public int Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    public int TimeoutMs
    {
        get => _timeoutMs;
        set => SetProperty(ref _timeoutMs, value);
    }

    public int SerialLength
    {
        get => _serialLength;
        set => SetProperty(ref _serialLength, value);
    }

    public bool ChecksumEnabled
    {
        get => _checksumEnabled;
        set => SetProperty(ref _checksumEnabled, value);
    }

    public bool MetricModeEnabled
    {
        get => _metricModeEnabled;
        set => SetProperty(ref _metricModeEnabled, value);
    }

    public bool ExtendedStatus
    {
        get => _extendedStatus;
        set => SetProperty(ref _extendedStatus, value);
    }

    public string LicenseKey
    {
        get => _licenseKey;
        set => SetProperty(ref _licenseKey, value);
    }
}
