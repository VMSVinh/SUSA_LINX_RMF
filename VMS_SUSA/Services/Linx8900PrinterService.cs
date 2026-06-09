using System.Net.Sockets;
using System.Text;
using VmsDevice.Printer.LinxCIJ.Commands;
using VmsDevice.Printer.LinxCIJ.Enum;
using VmsDevice.Printer.LinxCIJ.Helpers;
using VmsDevice.Helper;
using VMSSecurity;
using VMS_SUSA.Models;

namespace VMS_SUSA.Services;

public sealed class Linx8900PrinterService : IPrinterService, IDisposable
{
    public event EventHandler<PrinterTriggerReceivedEventArgs>? PrintTriggerReceived;
    private const int DefaultCommandTimeoutMs = 5000;
    private static readonly byte[] PrintTriggerSequence = [0x1B, 0x0F];
    private static readonly byte[] PacketTerminator = [0x1B, 0x03];

    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly SemaphoreSlim _triggerLock = new(1, 1);
    private readonly object _queueSync = new();
    private readonly object _receiveSync = new();
    private readonly Queue<QueuedSerial> _serialQueue = new();
    private readonly List<byte> _receiveBuffer = new();
    private readonly PrinterStatus _status = new();
    private readonly IPrinterDataLogService? _dataLogService;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;
    private TaskCompletionSource<byte[]>? _pendingResponse;
    private PrinterConfig _config = new();
    private bool _disposed;

    public Linx8900PrinterService(IPrinterDataLogService? dataLogService = null)
    {
        _dataLogService = dataLogService;
    }

    private sealed record QueuedSerial(string Serial, DateTime EnqueuedAt);

    private sealed record CommandResult(bool Success, byte[]? Response, string? ErrorMessage);

    public string LastError => _status.LastError;

    public async Task<bool> ConnectAsync(PrinterConfig config)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(Linx8900PrinterService));
        }

        await DisconnectAsync().ConfigureAwait(false);

        _config = config ?? new PrinterConfig();
        _status.LastError = string.Empty;

        try
        {
            if (!ValidateLicenseKey())
            {
                return false;
            }

            _client = new TcpClient();
            await _client.ConnectAsync(_config.IpAddress, _config.Port).ConfigureAwait(false);
            _stream = _client.GetStream();

            _listenerCts = new CancellationTokenSource();
            _listenerTask = Task.Run(() => ReceiveLoopAsync(_listenerCts.Token));

            _status.IsConnected = true;
            _status.IsPrinting = false;
            _status.LastReceivedRawData = string.Empty;
            _status.ReceivedRawDataLog = string.Empty;
            _status.LastUpdatedAt = DateTime.Now;
            _status.BufferCount = GetQueueCount();

            await TryRefreshCurrentMessagePrintCountAsync().ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            _status.IsConnected = false;
            _status.LastError = ex.Message;
            _status.LastUpdatedAt = DateTime.Now;
            return false;
        }
    }

    private bool ValidateLicenseKey()
    {
        if (string.IsNullOrWhiteSpace(_config.LicenseKey))
        {
            _status.LastError = "License key là bắt buộc.";
            _status.LastUpdatedAt = DateTime.Now;
            return false;
        }

        var macResult = MacAddressHelper.TryGetMacAddress(_config.IpAddress);
        if (!macResult.Success)
        {
            _status.LastError = $"Không lấy được MAC từ máy in: {macResult.ErrorMessage}";
            _status.LastUpdatedAt = DateTime.Now;
            return false;
        }

        var expectedLicense = KeyGenerator.GenerateShortKeyFromUNIC(macResult.MacAddress);
        var inputLicense = _config.LicenseKey.Trim();

        if (!string.Equals(expectedLicense, inputLicense, StringComparison.OrdinalIgnoreCase))
        {
            _status.LastError = "License key không khớp với MAC của thiết bị.";
            _status.LastUpdatedAt = DateTime.Now;
            return false;
        }

        return true;
    }

    public async Task DisconnectAsync()
    {
        if (_disposed)
        {
            return;
        }

        _listenerCts?.Cancel();

        try
        {
            _stream?.Close();
            _client?.Close();
        }
        catch
        {
            // Ignore close errors.
        }

        try
        {
            if (_listenerTask is not null)
            {
                try
                {
                    await _listenerTask.ConfigureAwait(false);
                }
                catch
                {
                    // Ignore listener shutdown exceptions.
                }
            }
        }
        finally
        {
            CancelPendingResponse();

            _stream = null;
            _client = null;
            _listenerTask = null;

            _listenerCts?.Dispose();
            _listenerCts = null;

            _status.IsConnected = false;
            _status.IsPrinting = false;
            _status.LastReceivedRawData = string.Empty;
            _status.ReceivedRawDataLog = string.Empty;
            _status.LastUpdatedAt = DateTime.Now;
        }
    }

    public async Task<bool> StartPrintAsync()
    {
        var result = await SendCommandAsync(LinxCommands.StartPrint).ConfigureAwait(false);
        if (!result.Success)
        {
            _status.LastError = result.ErrorMessage ?? "Failed to start printing.";
            _status.LastUpdatedAt = DateTime.Now;
            return false;
        }

        _status.IsPrinting = true;
        _status.LastError = string.Empty;
        _status.LastUpdatedAt = DateTime.Now;
        return true;
    }

    public async Task<bool> StopPrintAsync()
    {
        var result = await SendCommandAsync(LinxCommands.StopPrint).ConfigureAwait(false);
        if (!result.Success)
        {
            _status.LastError = result.ErrorMessage ?? "Failed to stop printing.";
            _status.LastUpdatedAt = DateTime.Now;
            return false;
        }

        _status.IsPrinting = false;
        _status.LastError = string.Empty;
        _status.LastUpdatedAt = DateTime.Now;
        return true;
    }

    public Task<bool> SendBufferAsync(IEnumerable<SerialItem> items)
    {
        if (!IsConnected())
        {
            _status.LastError = "Chưa kết nối máy in";
            _status.LastUpdatedAt = DateTime.Now;
            return Task.FromResult(false);
        }

        var serial = items
            .Where(x => x is not null && x.Status == SerialStatus.Waiting && !string.IsNullOrWhiteSpace(x.Serial))
            .Select(x => x.Serial.Trim())
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(serial))
        {
            return Task.FromResult(false);
        }

        lock (_queueSync)
        {
            _serialQueue.Enqueue(new QueuedSerial(serial, DateTime.Now));
        }

        _status.BufferCount = GetQueueCount();
        _status.LastUpdatedAt = DateTime.Now;
        return Task.FromResult(true);
    }

    public async Task<bool> Send1RemoteFieldDataAsync(string serial)
    {
        if (!IsConnected())
        {
            _status.LastError = "Chưa kết nối máy in";
            _status.LastUpdatedAt = DateTime.Now;
            return false;
        }

        if (string.IsNullOrWhiteSpace(serial))
        {
            _status.LastError = "Serial trống";
            _status.LastUpdatedAt = DateTime.Now;
            return false;
        }

        var fieldData = Encoding.UTF8.GetBytes(serial.Trim());
        var result = await SendDownloadRemoteFieldDataAsync(fieldData).ConfigureAwait(false);
        if (!result.Success)
        {
            _status.LastError = result.ErrorMessage ?? "Gửi remote field data thất bại.";
            _status.LastUpdatedAt = DateTime.Now;
            return false;
        }

        AppendRawLog(FormatRawFrame(result.Response));
        _status.SoftwareCounter += 1;
        _status.LastSentSerial = serial.Trim();
        _status.LastError = string.Empty;
        _status.LastUpdatedAt = DateTime.Now;
        return true;
    }

    public async Task<bool> ClearDataBufferAsync()
    {
        if (!IsConnected())
        {
            _status.LastError = "Chưa kết nối máy in";
            _status.LastUpdatedAt = DateTime.Now;
            return false;
        }

        var sent = await SendDownloadRemoteFieldDataNoResponseAsync(Array.Empty<byte>()).ConfigureAwait(false);
        if (!sent)
        {
            _status.LastError = "Xóa dữ liệu đệm thất bại.";
            _status.LastUpdatedAt = DateTime.Now;
            return false;
        }

        AppendRawLog("1B-02-1D-00-00-1B-03");

        lock (_queueSync)
        {
            _serialQueue.Clear();
        }

        _status.BufferCount = 0;
        _status.LastError = string.Empty;
        _status.LastUpdatedAt = DateTime.Now;
        return true;
    }

    public async Task<PrinterStatus> GetStatusAsync()
    {
        if (!IsConnected())
        {
            return SnapshotStatus();
        }

        var result = await SendCommandAsync(LinxCommands.PrinterStatusRequest).ConfigureAwait(false);
        if (result.Success && result.Response is { Length: > 0 })
        {
            UpdatePrinterStatusFromResponse(result.Response);
        }
        else if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            _status.LastError = result.ErrorMessage;
            _status.LastUpdatedAt = DateTime.Now;
        }

        await TryRefreshCurrentMessagePrintCountAsync().ConfigureAwait(false);

        _status.BufferCount = GetQueueCount();
        return SnapshotStatus();
    }

    public Task<bool> ResetErrorAsync()
    {
        _status.LastError = string.Empty;
        _status.LastUpdatedAt = DateTime.Now;
        return Task.FromResult(true);
    }

    public Task ResetSoftwareCounterAsync()
    {
        _status.SoftwareCounter = 0;
        _status.LastUpdatedAt = DateTime.Now;
        return Task.CompletedTask;
    }

    public Task SetSoftwareCounterAsync(int softwareCounter)
    {
        _status.SoftwareCounter = Math.Max(0, softwareCounter);
        _status.LastUpdatedAt = DateTime.Now;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _listenerCts?.Cancel();
        _stream?.Close();
        _client?.Close();
        _listenerCts?.Dispose();
        _commandLock.Dispose();
        _triggerLock.Dispose();
    }

    private bool IsConnected()
    {
        return _stream is not null && _client is not null;
    }

    private PrinterStatus SnapshotStatus()
    {
        return new PrinterStatus
        {
            IsConnected = _status.IsConnected,
            IsPrinting = _status.IsPrinting,
            PrinterCounter = _status.PrinterCounter,
            SoftwareCounter = _status.SoftwareCounter,
            BufferCount = _status.BufferCount,
            LastSentSerial = _status.LastSentSerial,
            LastPrintedSerial = _status.LastPrintedSerial,
            LastReceivedRawData = _status.LastReceivedRawData,
            ReceivedRawDataLog = _status.ReceivedRawDataLog,
            LastError = _status.LastError,
            LastUpdatedAt = _status.LastUpdatedAt
        };
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            return;
        }

        var readBuffer = new byte[4096];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead = await _stream.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), cancellationToken)
                    .ConfigureAwait(false);

                if (bytesRead <= 0)
                {
                    break;
                }

                lock (_receiveSync)
                {
                    for (int i = 0; i < bytesRead; i++)
                    {
                        _receiveBuffer.Add(readBuffer[i]);
                    }
                }

                await ProcessReceiveBufferAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during disconnect.
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                _status.LastError = $"Listener error: {ex.Message}";
                _status.LastUpdatedAt = DateTime.Now;
            }
        }
    }

    private async Task ProcessReceiveBufferAsync()
    {
        while (true)
        {
            byte[]? packet = null;
            bool printTrigger = false;

            lock (_receiveSync)
            {
                var triggerIndex = IndexOfSequence(_receiveBuffer, PrintTriggerSequence);
                var terminatorIndex = IndexOfSequence(_receiveBuffer, PacketTerminator);

                if (terminatorIndex >= 0 && (triggerIndex < 0 || terminatorIndex < triggerIndex))
                {
                    packet = _receiveBuffer.Take(terminatorIndex + 2).ToArray();
                    _receiveBuffer.RemoveRange(0, terminatorIndex + 2);
                }
                else if (triggerIndex >= 0)
                {
                    _receiveBuffer.RemoveRange(0, triggerIndex + 2);
                    printTrigger = true;
                }
                else
                {
                    break;
                }
            }

            if (packet is not null)
            {
                _status.LastReceivedRawData = BitConverter.ToString(packet);
                _status.LastUpdatedAt = DateTime.Now;
                AppendRawLog("[RX] " + BitConverter.ToString(packet));
                CompletePendingResponse(LinxPacketBuilder.RemoveEscapedBytes(packet));
                continue;
            }

            if (printTrigger)
            {
                _status.LastReceivedRawData = "1B-0F";
                _status.LastUpdatedAt = DateTime.Now;
                AppendRawLog("[TRIGGER] 1B-0F");
                IncrementPrinterCounter();
                _status.IsPrinting = true;
                PrintTriggerReceived?.Invoke(this, new PrinterTriggerReceivedEventArgs("1B-0F"));
                continue;
            }
        }
    }

    private async Task ProcessPrintTriggerAsync()
    {
        try
        {
            await _triggerLock.WaitAsync().ConfigureAwait(false);

            if (!IsConnected())
            {
                return;
            }

            QueuedSerial? nextSerial = null;
            lock (_queueSync)
            {
                if (_serialQueue.Count > 0)
                {
                    nextSerial = _serialQueue.Peek();
                }
            }

            if (nextSerial is null)
            {
                return;
            }

            _status.IsPrinting = true;
            _status.LastUpdatedAt = DateTime.Now;

            var fieldData = Encoding.UTF8.GetBytes(nextSerial.Serial);
            var sendResult = await SendDownloadRemoteFieldDataAsync(fieldData).ConfigureAwait(false);
            if (!sendResult.Success)
            {
                _status.LastError = sendResult.ErrorMessage ?? "Gửi remote field data thất bại";
                _status.LastUpdatedAt = DateTime.Now;
                return;
            }

            AppendRawLog(FormatRawFrame(sendResult.Response));

            lock (_queueSync)
            {
                if (_serialQueue.Count > 0 && _serialQueue.Peek().Serial == nextSerial.Serial)
                {
                    _serialQueue.Dequeue();
                }
            }

            _status.SoftwareCounter += 1;
            _status.BufferCount = GetQueueCount();
            _status.LastSentSerial = nextSerial.Serial;
            _status.LastPrintedSerial = nextSerial.Serial;
            _status.LastError = string.Empty;
            _status.LastUpdatedAt = DateTime.Now;
        }
        catch (OperationCanceledException)
        {
            // Ignore shutdown cancellation.
        }
        catch (ObjectDisposedException)
        {
            // Ignore shutdown disposal.
        }
        catch (Exception ex)
        {
            _status.LastError = $"Trigger handler error: {ex.Message}";
            _status.LastUpdatedAt = DateTime.Now;
        }
        finally
        {
            if (_triggerLock.CurrentCount == 0)
            {
                _triggerLock.Release();
            }
        }
    }

    private async Task<CommandResult> SendDownloadRemoteFieldDataAsync(byte[] fieldData)
    {
        var payload = new List<byte>(fieldData.Length + 2);
        payload.AddRange(BitConverter.GetBytes((ushort)fieldData.Length));
        payload.AddRange(fieldData);

        return await SendCommandAsync(
                LinxCommands.DownloadRemoteFieldData,
                payload.ToArray())
            .ConfigureAwait(false);
    }

    private async Task<bool> SendDownloadRemoteFieldDataNoResponseAsync(byte[] fieldData)
    {
        var payload = new List<byte>(fieldData.Length + 2);
        payload.AddRange(BitConverter.GetBytes((ushort)fieldData.Length));
        payload.AddRange(fieldData);

        if (_stream is null || !_stream.CanWrite)
        {
            return false;
        }

        await _commandLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var packet = LinxPacketBuilder.BuildPacket(
                LinxCommands.DownloadRemoteFieldData,
                payload.ToArray(),
                GetDelimiter(),
                _config.ChecksumEnabled);

            await _stream.WriteAsync(packet).ConfigureAwait(false);

#if DEBUG
            Console.WriteLine($"[SEND] {BitConverter.ToString(packet)}");
#endif
            return true;
        }
        catch (Exception ex)
        {
            _status.LastError = $"Unhandled exception: {ex.Message}";
            _status.LastUpdatedAt = DateTime.Now;
            return false;
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task<CommandResult> SendCommandAsync(
        byte commandId,
        byte[]? payload = null,
        int timeoutMs = DefaultCommandTimeoutMs,
        CancellationToken cancellationToken = default)
    {
        if (_stream is null || !_stream.CanWrite)
        {
            return new CommandResult(false, null, "Chưa kết nối đến máy in.");
        }

        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        TaskCompletionSource<byte[]>? localPending = null;

        try
        {
            var packet = LinxPacketBuilder.BuildPacket(commandId, payload, GetDelimiter(), _config.ChecksumEnabled);
            localPending = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_receiveSync)
            {
                _pendingResponse = localPending;
            }

            await _stream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);

#if DEBUG
            Console.WriteLine($"[SEND] {BitConverter.ToString(packet)}");
#endif

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);

            var completedTask = await Task.WhenAny(
                    localPending.Task,
                    Task.Delay(Timeout.Infinite, timeoutCts.Token))
                .ConfigureAwait(false);

            if (completedTask != localPending.Task)
            {
                lock (_receiveSync)
                {
                    if (ReferenceEquals(_pendingResponse, localPending))
                    {
                        _pendingResponse = null;
                    }
                }

                return new CommandResult(false, null, "Read operation timed out.");
            }

            var response = await localPending.Task.ConfigureAwait(false);
            var decodedResponse = LinxPacketBuilder.RemoveEscapedBytes(response);

#if DEBUG
            Console.WriteLine($"[RECV] {BitConverter.ToString(decodedResponse)}");
#endif

            if (decodedResponse.Length == 0)
            {
                return new CommandResult(false, decodedResponse, "Empty response from printer.");
            }

            if (_config.ChecksumEnabled && !LinxPacketBuilder.VerifyChecksum(decodedResponse))
            {
                return new CommandResult(false, decodedResponse, "Invalid checksum in response.");
            }

            if (decodedResponse.Length >= 3 && decodedResponse[0] == 0x1B && decodedResponse[1] == 0x15)
            {
                return new CommandResult(false, decodedResponse, $"Printer/Command error: 0x{decodedResponse[2]:X2}");
            }

            return new CommandResult(true, decodedResponse, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new CommandResult(false, null, "Operation cancelled.");
        }
        catch (Exception ex)
        {
            return new CommandResult(false, null, $"Unhandled exception: {ex.Message}");
        }
        finally
        {
            lock (_receiveSync)
            {
                if (localPending is not null && ReferenceEquals(_pendingResponse, localPending))
                {
                    _pendingResponse = null;
                }
            }

            _commandLock.Release();
        }
    }

    private void CompletePendingResponse(byte[] response)
    {
        TaskCompletionSource<byte[]>? pending;

        lock (_receiveSync)
        {
            pending = _pendingResponse;
            _pendingResponse = null;
        }

        pending?.TrySetResult(response);
    }

    private void CancelPendingResponse()
    {
        TaskCompletionSource<byte[]>? pending;

        lock (_receiveSync)
        {
            pending = _pendingResponse;
            _pendingResponse = null;
        }

        pending?.TrySetCanceled();
    }

    private void UpdatePrinterStatusFromResponse(byte[] response)
    {
        if (response.Length < 11)
        {
            return;
        }

        var printState = (PrintState)response[6];
        var jetState = (JetState)response[5];

        _status.IsPrinting = printState is PrintState.Printing
            or PrintState.GeneratingPixels
            or PrintState.Waiting
            or PrintState.Last
            or PrintState.PrintingAndGeneratingPixels;

        if (jetState is JetState.JetStopped or JetState.Fault)
        {
            _status.IsPrinting = false;
        }

        _status.LastUpdatedAt = DateTime.Now;
    }

    private void AppendRawLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        _status.ReceivedRawDataLog = line;
        _status.LastUpdatedAt = DateTime.Now;
        _ = _dataLogService?.AppendRawAsync(line);
    }

    private static string FormatRawFrame(byte[]? response)
    {
        return response is { Length: > 0 }
            ? BitConverter.ToString(response)
            : string.Empty;
    }

    private void IncrementPrinterCounter()
    {
        _status.PrinterCounter += 1;
        _status.LastUpdatedAt = DateTime.Now;
    }

    private async Task<bool> TryRefreshCurrentMessagePrintCountAsync()
    {
        var result = await SendCommandAsync(
                LinxCommands.RequestMessagePrintCount,
                new byte[16])
            .ConfigureAwait(false);

        if (!result.Success || result.Response is not { Length: >= 9 })
        {
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                _status.LastError = result.ErrorMessage;
                _status.LastUpdatedAt = DateTime.Now;
            }

            return false;
        }

        var messagePrintCount = BitConverter.ToUInt32(result.Response, 5);
        _status.PrinterCounter = messagePrintCount > int.MaxValue
            ? int.MaxValue
            : (int)messagePrintCount;
        _status.LastUpdatedAt = DateTime.Now;
        return true;
    }

    private byte GetDelimiter()
    {
        return _config.ExtendedStatus ? (byte)0x01 : (byte)0x02;
    }

    private int GetQueueCount()
    {
        lock (_queueSync)
        {
            return _serialQueue.Count;
        }
    }

    private static int IndexOfSequence(IReadOnlyList<byte> source, ReadOnlySpan<byte> sequence)
    {
        if (sequence.Length == 0 || source.Count < sequence.Length)
        {
            return -1;
        }

        for (var i = 0; i <= source.Count - sequence.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < sequence.Length; j++)
            {
                if (source[i + j] != sequence[j])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return i;
            }
        }

        return -1;
    }
}











