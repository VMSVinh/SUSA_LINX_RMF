using VMS_SUSA.Models;

namespace VMS_SUSA.Services;

public sealed class MockPrinterService : IPrinterService
{
    private readonly Random _random = new();
    private readonly object _sync = new();
    private IList<SerialItem> _serialItems = new List<SerialItem>();
    private bool _isConnected;
    private bool _isPrinting;
    private int _printerCounter;
    private int _softwareCounter;
    private int _bufferCount;
    private string _lastSentSerial = string.Empty;
    private string _lastPrintedSerial = string.Empty;
    private string _lastError = string.Empty;
    private DateTime _lastUpdatedAt = DateTime.Now;

    public void BindSerialItems(IList<SerialItem> serialItems)
    {
        lock (_sync)
        {
            _serialItems = serialItems;
        }
    }

    public async Task<bool> ConnectAsync(PrinterConfig config)
    {
        await Task.Delay(500);
        lock (_sync)
        {
            _isConnected = true;
            _lastError = string.Empty;
            _lastUpdatedAt = DateTime.Now;
        }

        return true;
    }

    public Task DisconnectAsync()
    {
        lock (_sync)
        {
            _isConnected = false;
            _isPrinting = false;
            _bufferCount = 0;
            _lastUpdatedAt = DateTime.Now;
        }

        return Task.CompletedTask;
    }

    public Task<bool> StartPrintAsync()
    {
        lock (_sync)
        {
            if (!_isConnected)
            {
                return Task.FromResult(false);
            }

            _isPrinting = true;
            _lastUpdatedAt = DateTime.Now;
        }

        return Task.FromResult(true);
    }

    public Task<bool> StopPrintAsync()
    {
        lock (_sync)
        {
            if (!_isConnected)
            {
                return Task.FromResult(false);
            }

            _isPrinting = false;
            _lastUpdatedAt = DateTime.Now;
        }

        return Task.FromResult(true);
    }

    public Task<bool> SendBufferAsync(IEnumerable<SerialItem> items)
    {
        var bufferItems = items.ToList();
        lock (_sync)
        {
            if (!_isConnected || bufferItems.Count == 0)
            {
                _lastError = "Không thể gửi buffer";
                return Task.FromResult(false);
            }

            _softwareCounter += bufferItems.Count;
            _bufferCount = Math.Max(0, _bufferCount + bufferItems.Count);
            _lastSentSerial = bufferItems.Last().Serial;
            _lastError = string.Empty;
            _lastUpdatedAt = DateTime.Now;

            foreach (var item in bufferItems)
            {
                item.Status = SerialStatus.Sent;
                item.SentAt = DateTime.Now;
                item.Note = string.Empty;
            }
        }

        return Task.FromResult(true);
    }

    public Task<PrinterStatus> GetStatusAsync()
    {
        lock (_sync)
        {
            if (_isConnected)
            {
                _printerCounter += _random.Next(0, 3);

                var sentItems = _serialItems.Where(x => x.Status == SerialStatus.Sent).OrderBy(x => x.SentAt).ToList();
                var printedCount = Math.Min(sentItems.Count, _random.Next(0, 3));
                for (var i = 0; i < printedCount; i++)
                {
                    var item = sentItems[i];
                    item.Status = SerialStatus.Printed;
                    item.PrintedAt = DateTime.Now;
                    _lastPrintedSerial = item.Serial;
                }

                _bufferCount = _serialItems.Count(x => x.Status == SerialStatus.Sent);
                _lastUpdatedAt = DateTime.Now;
            }

            return Task.FromResult(new PrinterStatus
            {
                IsConnected = _isConnected,
                IsPrinting = _isPrinting,
                PrinterCounter = _printerCounter,
                SoftwareCounter = _softwareCounter,
                BufferCount = _bufferCount,
                LastSentSerial = _lastSentSerial,
                LastPrintedSerial = _lastPrintedSerial,
                LastError = _lastError,
                LastUpdatedAt = _lastUpdatedAt
            });
        }
    }

    public Task<bool> ResetErrorAsync()
    {
        lock (_sync)
        {
            _lastError = string.Empty;
            _lastUpdatedAt = DateTime.Now;
        }

        return Task.FromResult(true);
    }
}
