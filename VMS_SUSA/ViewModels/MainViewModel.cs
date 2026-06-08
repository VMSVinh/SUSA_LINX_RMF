using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using VMS_SUSA.Models;
using VMS_SUSA.Services;

namespace VMS_SUSA.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly IPrinterService _printerService;
    private readonly IAppStateService _appStateService;
    private readonly IPrinterDataLogService _printerDataLogService;
    private readonly IFileDialogService _fileDialogService;
    private readonly DispatcherTimer _clockTimer;
    private readonly List<IRelayCommand> _relayCommands = new();

    private PrinterConfig _printerConfig = new();
    private PrinterStatus _printerStatus = new();
    private ObservableCollection<SerialItem> _serialItems = new();
    private ObservableCollection<SerialItem> _filteredSerialItems = new();
    private ObservableCollection<StatusFilterOption> _statusFilterOptions = new();

    private string _importFilePath = string.Empty;
    private string _searchText = string.Empty;
    private SerialStatus? _selectedStatusFilter;
    private string _currentTimeText = string.Empty;
    private string _systemWarningText = "Chưa có dữ liệu serial";
    private DateTime _lastSavedAt;
    private int _totalImportedCount;
    private int _displayedCount;
    private int _appStatePrinterCounter;
    private bool _isConnecting;
    private bool _isInitialized;
    private bool _isRefreshingStatus;
    private readonly SemaphoreSlim _remoteFieldSendLock = new(1, 1);
    private readonly SemaphoreSlim _bulkSendLock = new(1, 1);

    public MainViewModel(IPrinterService printerService, IAppStateService appStateService, IFileDialogService fileDialogService, IPrinterDataLogService printerDataLogService)
    {
        _printerService = printerService;
        _appStateService = appStateService;
        _fileDialogService = fileDialogService;
        _printerDataLogService = printerDataLogService;
        _printerService.PrintTriggerReceived += PrinterService_PrintTriggerReceived;

        StatusFilterOptions = new ObservableCollection<StatusFilterOption>
        {
            new() { DisplayName = "Tất cả", Value = null },
            new() { DisplayName = "Chờ gửi", Value = SerialStatus.Waiting },
            new() { DisplayName = "Đã gửi", Value = SerialStatus.Sent },
            new() { DisplayName = "Đã in", Value = SerialStatus.Printed },
            new() { DisplayName = "Lỗi", Value = SerialStatus.Error },
            new() { DisplayName = "Trùng lặp", Value = SerialStatus.Duplicate },
            new() { DisplayName = "Sai định dạng", Value = SerialStatus.Invalid }
        };

        BrowseImportFileCommand = new AsyncRelayCommand(BrowseImportFileAsync);
        ImportSerialFileCommand = new AsyncRelayCommand(ImportSerialFileAsync, () => !string.IsNullOrWhiteSpace(ImportFilePath));
        ClearSerialDataCommand = new AsyncRelayCommand(ClearSerialDataAsync, () => SerialItems.Count > 0);
        CheckDuplicateCommand = new AsyncRelayCommand(CheckDuplicateAsync);
        ExportErrorDataCommand = new AsyncRelayCommand(ExportErrorDataAsync, () => SerialItems.Count > 0);

        SaveConfigCommand = new AsyncRelayCommand(SaveConfigAsync);
        ReloadConfigCommand = new AsyncRelayCommand(ReloadConfigAsync);
        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync, () => !IsConnecting);

        ConnectPrinterCommand = new AsyncRelayCommand(ConnectPrinterAsync, () => CanConnect);
        DisconnectPrinterCommand = new AsyncRelayCommand(DisconnectPrinterAsync, () => CanDisconnect);
        StartPrintCommand = new AsyncRelayCommand(StartPrintAsync, () => CanStartPrint);
        StopPrintCommand = new AsyncRelayCommand(StopPrintAsync, () => CanStopPrint);
        SendBufferCommand = new AsyncRelayCommand(SendBufferAsync, () => CanSendBuffer);
        Send1RemoteFieldDataCommand = new AsyncRelayCommand(Send1RemoteFieldDataAsync, () => CanSendBuffer);
        Send30RemoteFieldsThenStartPrintCommand = new AsyncRelayCommand(Send30RemoteFieldsThenStartPrintAsync, () => CanSend30RemoteFieldsThenStartPrint);
        ClearDataBufferCommand = new AsyncRelayCommand(ClearDataBufferAsync, () => IsConnected);
        GetPrinterStatusCommand = new AsyncRelayCommand(GetPrinterStatusAsync, () => IsConnected);
        ResetErrorCommand = new AsyncRelayCommand(ResetErrorAsync, () => IsConnected || !string.IsNullOrWhiteSpace(PrinterStatus.LastError));

        _relayCommands.AddRange(new[]
        {
            BrowseImportFileCommand,
            ImportSerialFileCommand,
            ClearSerialDataCommand,
            CheckDuplicateCommand,
            ExportErrorDataCommand,
            SaveConfigCommand,
            ReloadConfigCommand,
            TestConnectionCommand,
            ConnectPrinterCommand,
            DisconnectPrinterCommand,
            StartPrintCommand,
            StopPrintCommand,
            SendBufferCommand,
            Send1RemoteFieldDataCommand,
            Send30RemoteFieldsThenStartPrintCommand,
            ClearDataBufferCommand,
            GetPrinterStatusCommand,
            ResetErrorCommand
        });

        SerialItems.CollectionChanged += SerialItems_CollectionChanged;
        FilteredSerialItems = new ObservableCollection<SerialItem>();
        _clockTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _clockTimer.Tick += ClockTimer_Tick;

        UpdateCurrentTime();
        UpdateDerivedState();
        RefreshFilteredSerialItems();
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        var state = await _appStateService.LoadAsync();
        var savedSerialItems = await _printerDataLogService.LoadSerialItemsAsync();

        PrinterConfig = state?.PrinterConfig ?? new PrinterConfig();
        PrinterStatus = state?.PrinterStatus ?? new PrinterStatus();
        _appStatePrinterCounter = state?.PrinterStatus?.PrinterCounter ?? 0;
        PrinterStatus.IsConnected = false;
        PrinterStatus.IsPrinting = false;
        PrinterStatus.LastReceivedRawData = string.Empty;
        PrinterStatus.ReceivedRawDataLog = string.Empty;
        OnPropertyChanged(nameof(PrintedCount));

        if (savedSerialItems is { Count: > 0 })
        {
            SerialItems = new ObservableCollection<SerialItem>(savedSerialItems);
            TotalImportedCount = SerialItems.Count;
        }
        else if (state is null)
        {
            LoadDemoData();
            UpdateDerivedState();
            RefreshFilteredSerialItems();
            _lastSavedAt = default;
            _clockTimer.Start();
            return;
        }
        else
        {
            SerialItems = new ObservableCollection<SerialItem>();
            TotalImportedCount = 0;
        }

        UpdateDerivedState();
        RefreshFilteredSerialItems();
        UpdateCurrentTime();
        _lastSavedAt = state?.SavedAt ?? default;
        _clockTimer.Start();
        OnPropertyChanged(nameof(LastStateSavedText));
    }

    public PrinterConfig PrinterConfig
    {
        get => _printerConfig;
        set
        {
            if (_printerConfig is not null)
            {
                _printerConfig.PropertyChanged -= PrinterConfig_PropertyChanged;
            }

            _printerConfig = value ?? new PrinterConfig();
            _printerConfig.PropertyChanged += PrinterConfig_PropertyChanged;
            OnPropertyChanged();
            UpdateCommandStates();
            UpdateDerivedState();
        }
    }

    public PrinterStatus PrinterStatus
    {
        get => _printerStatus;
        set
        {
            if (_printerStatus is not null)
            {
                _printerStatus.PropertyChanged -= PrinterStatus_PropertyChanged;
            }

            _printerStatus = value ?? new PrinterStatus();
            _printerStatus.PropertyChanged += PrinterStatus_PropertyChanged;
            OnPropertyChanged();
            UpdateCommandStates();
            UpdateDerivedState();
        }
    }

    public ObservableCollection<SerialItem> SerialItems
    {
        get => _serialItems;
        set
        {
            if (_serialItems is not null)
            {
                _serialItems.CollectionChanged -= SerialItems_CollectionChanged;
            }

            _serialItems = value ?? new ObservableCollection<SerialItem>();
            _serialItems.CollectionChanged += SerialItems_CollectionChanged;
            OnPropertyChanged();
            ReindexSerialItems();
            UpdateDerivedState();
            RefreshFilteredSerialItems();
            BindMockPrinterItems();
        }
    }

    public ObservableCollection<SerialItem> FilteredSerialItems
    {
        get => _filteredSerialItems;
        set => SetProperty(ref _filteredSerialItems, value);
    }

    public ObservableCollection<StatusFilterOption> StatusFilterOptions
    {
        get => _statusFilterOptions;
        set => SetProperty(ref _statusFilterOptions, value);
    }

    public string ImportFilePath
    {
        get => _importFilePath;
        set
        {
            if (SetProperty(ref _importFilePath, value))
            {
                ImportSerialFileCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                RefreshFilteredSerialItems();
            }
        }
    }

    public SerialStatus? SelectedStatusFilter
    {
        get => _selectedStatusFilter;
        set
        {
            if (SetProperty(ref _selectedStatusFilter, value))
            {
                RefreshFilteredSerialItems();
            }
        }
    }

    public bool IsConnecting => _isConnecting;

    public bool IsConnected => PrinterStatus.IsConnected;

    public bool IsPrinting => PrinterStatus.IsPrinting;

    public int TotalImportedCount
    {
        get => _totalImportedCount;
        private set => SetProperty(ref _totalImportedCount, value);
    }

    public int ValidCount => SerialItems.Count(x => x.Status is SerialStatus.Waiting or SerialStatus.Sent or SerialStatus.Printed);

    public int DuplicateCount => SerialItems.Count(x => x.Status == SerialStatus.Duplicate);

    public int InvalidCount => SerialItems.Count(x => x.Status == SerialStatus.Invalid);

    public int WaitingCount => SerialItems.Count(x => x.Status == SerialStatus.Waiting);

    public int SentCount => SerialItems.Count(x => x.Status == SerialStatus.Sent);

    public int PrintedCount => IsConnected
        ? PrinterStatus.PrinterCounter
        : _appStatePrinterCounter;

    public int ErrorCount => SerialItems.Count(x => x.Status == SerialStatus.Error);

    public int RemainingCount => WaitingCount;

    public int DisplayedCount
    {
        get => _displayedCount;
        private set => SetProperty(ref _displayedCount, value);
    }

    public bool CanConnect => !IsConnected && !IsConnecting;

    public bool CanDisconnect => IsConnected;

    public bool CanStartPrint => IsConnected && !IsConnecting;

    public bool CanStopPrint => IsConnected && IsPrinting;

    public bool CanSendBuffer => IsConnected && WaitingCount > 0;

    public bool CanSend30RemoteFieldsThenStartPrint => IsConnected && WaitingCount >= 30;

    public bool CanEditConfig => !IsPrinting;

    private bool ShouldStopPrintNow => IsConnected
        && IsPrinting
        && ValidCount > 0
        && PrinterStatus.PrinterCounter >= ValidCount;

    public string CurrentTimeText
    {
        get => _currentTimeText;
        private set => SetProperty(ref _currentTimeText, value);
    }

    public string SystemWarningText
    {
        get => _systemWarningText;
        private set => SetProperty(ref _systemWarningText, value);
    }

    public string ConnectionStatusText => _isConnecting
        ? "Đang kết nối"
        : PrinterStatus.IsConnected
            ? "Đã kết nối"
            : string.IsNullOrWhiteSpace(PrinterStatus.LastError)
                ? "Chưa kết nối"
                : "Mất kết nối";

    public string PrintStatusText => PrinterStatus.IsPrinting
        ? "Đang in"
        : string.IsNullOrWhiteSpace(PrinterStatus.LastError)
            ? "Dừng"
            : "Lỗi";

    public Brush ConnectionStatusBrush => _isConnecting
        ? Brushes.Goldenrod
        : PrinterStatus.IsConnected
            ? Brushes.SeaGreen
            : string.IsNullOrWhiteSpace(PrinterStatus.LastError)
                ? Brushes.Gray
                : Brushes.IndianRed;

    public Brush PrintStatusBrush => PrinterStatus.IsPrinting
        ? Brushes.SeaGreen
        : string.IsNullOrWhiteSpace(PrinterStatus.LastError)
            ? Brushes.Gray
            : Brushes.IndianRed;

    public Brush WarningBrush => InvalidCount > 0 || DuplicateCount > 0 ? Brushes.Goldenrod : Brushes.SeaGreen;

    public string LastStateSavedText => _lastSavedAt == default
        ? string.Empty
        : $"Lưu gần nhất: {_lastSavedAt:dd/MM/yyyy HH:mm:ss}";

    public IRelayCommand BrowseImportFileCommand { get; }
    public IRelayCommand ImportSerialFileCommand { get; }
    public IRelayCommand ClearSerialDataCommand { get; }
    public IRelayCommand CheckDuplicateCommand { get; }
    public IRelayCommand ExportErrorDataCommand { get; }

    public IRelayCommand SaveConfigCommand { get; }
    public IRelayCommand ReloadConfigCommand { get; }
    public IRelayCommand TestConnectionCommand { get; }

    public IRelayCommand ConnectPrinterCommand { get; }
    public IRelayCommand DisconnectPrinterCommand { get; }
    public IRelayCommand StartPrintCommand { get; }
    public IRelayCommand StopPrintCommand { get; }
    public IRelayCommand SendBufferCommand { get; }
    public IRelayCommand Send1RemoteFieldDataCommand { get; }
    public IRelayCommand Send30RemoteFieldsThenStartPrintCommand { get; }
    public IRelayCommand ClearDataBufferCommand { get; }
    public IRelayCommand GetPrinterStatusCommand { get; }
    public IRelayCommand ResetErrorCommand { get; }

    private void PrinterConfig_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        UpdateCommandStates();
        UpdateDerivedState();
    }

    private void PrinterStatus_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsPrinting));
        OnPropertyChanged(nameof(ConnectionStatusText));
        OnPropertyChanged(nameof(PrintStatusText));
        OnPropertyChanged(nameof(ConnectionStatusBrush));
        OnPropertyChanged(nameof(PrintStatusBrush));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
        OnPropertyChanged(nameof(CanStartPrint));
        OnPropertyChanged(nameof(CanStopPrint));
        OnPropertyChanged(nameof(CanSendBuffer));
        OnPropertyChanged(nameof(CanSend30RemoteFieldsThenStartPrint));
        OnPropertyChanged(nameof(ClearDataBufferCommand));
        OnPropertyChanged(nameof(CanEditConfig));
        OnPropertyChanged(nameof(LastStateSavedText));
        UpdateCommandStates();
        UpdateDerivedState();
    }

    private void SerialItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (SerialItem item in e.NewItems)
            {
                item.Index = item.Index <= 0 ? SerialItems.IndexOf(item) + 1 : item.Index;
            }
        }

        ReindexSerialItems();
        UpdateDerivedState();
        RefreshFilteredSerialItems();
    }

    private void ClockTimer_Tick(object? sender, EventArgs e)
    {
        UpdateCurrentTime();

        if (IsConnected && !_isRefreshingStatus)
        {
            _ = RefreshPrinterStatusAsync();
        }
    }

    private void PrinterService_PrintTriggerReceived(object? sender, PrinterTriggerReceivedEventArgs e)
    {
        if (Application.Current?.Dispatcher is null)
        {
            _ = HandlePrinterTriggerAsync(e.RawData);
            return;
        }

        _ = Application.Current.Dispatcher.InvokeAsync(() => HandlePrinterTriggerAsync(e.RawData));
    }

    private void UpdateCurrentTime()
    {
        CurrentTimeText = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");
    }

    private void UpdateDerivedState()
    {
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsPrinting));
        OnPropertyChanged(nameof(ValidCount));
        OnPropertyChanged(nameof(DuplicateCount));
        OnPropertyChanged(nameof(InvalidCount));
        OnPropertyChanged(nameof(WaitingCount));
        OnPropertyChanged(nameof(SentCount));
        OnPropertyChanged(nameof(PrintedCount));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(RemainingCount));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
        OnPropertyChanged(nameof(CanStartPrint));
        OnPropertyChanged(nameof(CanStopPrint));
        OnPropertyChanged(nameof(CanSendBuffer));
        OnPropertyChanged(nameof(CanSend30RemoteFieldsThenStartPrint));
        OnPropertyChanged(nameof(ClearDataBufferCommand));
        OnPropertyChanged(nameof(CanEditConfig));
        OnPropertyChanged(nameof(ConnectionStatusText));
        OnPropertyChanged(nameof(PrintStatusText));
        OnPropertyChanged(nameof(ConnectionStatusBrush));
        OnPropertyChanged(nameof(PrintStatusBrush));
        OnPropertyChanged(nameof(WarningBrush));
        OnPropertyChanged(nameof(SystemWarningText));
        OnPropertyChanged(nameof(LastStateSavedText));
        RefreshWarningText();
        DisplayedCount = FilteredSerialItems.Count;
        UpdateCommandStates();
    }

    private void RefreshWarningText()
    {
        if (TotalImportedCount == 0)
        {
            SystemWarningText = "Chưa có dữ liệu serial";
            return;
        }

        if (InvalidCount > 0)
        {
            SystemWarningText = "Có serial sai độ dài";
            return;
        }

        if (DuplicateCount > 0)
        {
            SystemWarningText = "Có serial bị trùng";
            return;
        }

        if (WaitingCount == 0)
        {
            SystemWarningText = "Đã hết dữ liệu in";
            return;
        }

        SystemWarningText = "Sẵn sàng gửi dữ liệu";
    }

    private void UpdateCommandStates()
    {
        foreach (var command in _relayCommands)
        {
            command.NotifyCanExecuteChanged();
        }

        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
        OnPropertyChanged(nameof(CanStartPrint));
        OnPropertyChanged(nameof(CanStopPrint));
        OnPropertyChanged(nameof(CanSendBuffer));
        OnPropertyChanged(nameof(CanSend30RemoteFieldsThenStartPrint));
        OnPropertyChanged(nameof(ClearDataBufferCommand));
        OnPropertyChanged(nameof(CanEditConfig));
    }

    private void RefreshFilteredSerialItems()
    {
        var query = SearchText?.Trim() ?? string.Empty;
        var items = SerialItems
            .Where(item =>
            {
                var matchSearch = string.IsNullOrWhiteSpace(query) ||
                                  item.Serial.Contains(query, StringComparison.OrdinalIgnoreCase);
                var matchStatus = !SelectedStatusFilter.HasValue || item.Status == SelectedStatusFilter.Value;
                return matchSearch && matchStatus;
            })
            .ToList();

        FilteredSerialItems.Clear();
        foreach (var item in items)
        {
            FilteredSerialItems.Add(item);
        }

        DisplayedCount = FilteredSerialItems.Count;
        OnPropertyChanged(nameof(DisplayedCount));
    }

    private void ReindexSerialItems()
    {
        for (var i = 0; i < SerialItems.Count; i++)
        {
            SerialItems[i].Index = i + 1;
        }
    }

    private void BindMockPrinterItems()
    {
        if (_printerService is MockPrinterService mockPrinterService)
        {
            mockPrinterService.BindSerialItems(SerialItems);
        }
    }

    private async Task BrowseImportFileAsync()
    {
        var filePath = await _fileDialogService.OpenTextFileAsync();
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            ImportFilePath = filePath;
            await ImportSerialFileAsync();
        }
    }

    private async Task ImportSerialFileAsync()
    {
        if (string.IsNullOrWhiteSpace(ImportFilePath) || !File.Exists(ImportFilePath))
        {
            MessageBox.Show("Vui lòng chọn file .txt hợp lệ.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var lines = await File.ReadAllLinesAsync(ImportFilePath, Encoding.UTF8);
        var serialLength = Math.Max(1, PrinterConfig.SerialLength);
        var validSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var importedItems = new List<SerialItem>();
        var totalLines = lines.Length;
        var index = 0;

        foreach (var rawLine in lines)
        {
            var serial = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(serial))
            {
                continue;
            }

            index++;
            var item = new SerialItem
            {
                Index = index,
                Serial = serial
            };

            if (serial.Length != serialLength)
            {
                item.Status = SerialStatus.Invalid;
                item.Note = "Sai độ dài";
            }
            else if (!validSet.Add(serial))
            {
                item.Status = SerialStatus.Duplicate;
                item.Note = "Serial trùng";
            }
            else
            {
                item.Status = SerialStatus.Waiting;
                item.Note = string.Empty;
            }

            importedItems.Add(item);
        }

        SerialItems = new ObservableCollection<SerialItem>(importedItems);
        TotalImportedCount = totalLines;
        await _printerService.ResetSoftwareCounterAsync();
        PrinterStatus.SoftwareCounter = 0;
        PrinterStatus.BufferCount = 0;
        PrinterStatus.LastSentSerial = string.Empty;
        PrinterStatus.LastPrintedSerial = string.Empty;
        PrinterStatus.LastError = string.Empty;
        PrinterStatus.LastUpdatedAt = DateTime.Now;
        ReindexSerialItems();
        RefreshFilteredSerialItems();
        UpdateDerivedState();
        await SaveStateAsync();
        MessageBox.Show($"Đã import {importedItems.Count} serial hợp lệ/không hợp lệ từ {totalLines} dòng.", "Import dữ liệu", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task ClearSerialDataAsync()
    {
        SerialItems.Clear();
        TotalImportedCount = 0;
        ImportFilePath = string.Empty;
        PrinterStatus.SoftwareCounter = 0;
        PrinterStatus.BufferCount = 0;
        PrinterStatus.LastSentSerial = string.Empty;
        PrinterStatus.LastPrintedSerial = string.Empty;
        PrinterStatus.LastError = string.Empty;
        PrinterStatus.LastUpdatedAt = DateTime.Now;
        RefreshFilteredSerialItems();
        UpdateDerivedState();
        await SaveStateAsync();
    }

    private async Task CheckDuplicateAsync()
    {
        var serialLength = Math.Max(1, PrinterConfig.SerialLength);
        var validSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in SerialItems)
        {
            if (item.Status is SerialStatus.Invalid or SerialStatus.Error)
            {
                continue;
            }

            if (item.Serial.Trim().Length != serialLength)
            {
                item.Status = SerialStatus.Invalid;
                item.Note = "Sai độ dài";
                continue;
            }

            if (!validSet.Add(item.Serial))
            {
                item.Status = SerialStatus.Duplicate;
                item.Note = "Serial trùng";
            }
            else if (item.Status == SerialStatus.Duplicate)
            {
                item.Status = SerialStatus.Waiting;
                item.Note = string.Empty;
            }
        }

        RefreshFilteredSerialItems();
        UpdateDerivedState();
        await SaveStateAsync();
    }

    private async Task ExportErrorDataAsync()
    {
        if (SerialItems.Count == 0)
        {
            MessageBox.Show("Không có dữ liệu để xuất.", "Xuất dữ liệu lỗi", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var outputPath = await _fileDialogService.SaveTextFileAsync("serial_errors.txt");
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        var errorLines = SerialItems
            .Where(x => x.Status is SerialStatus.Invalid or SerialStatus.Duplicate or SerialStatus.Error)
            .Select(x => $"{x.Index}\t{x.Serial}\t{x.StatusText}\t{x.Note}")
            .ToArray();

        await File.WriteAllLinesAsync(outputPath, errorLines, Encoding.UTF8);
        MessageBox.Show($"Đã xuất {errorLines.Length} dòng lỗi.", "Xuất dữ liệu lỗi", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task SaveConfigAsync()
    {
        await SaveStateAsync();
        MessageBox.Show("Đã lưu cấu hình và trạng thái hiện tại.", "Lưu cấu hình", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task ReloadConfigAsync()
    {
        var state = await _appStateService.LoadAsync();
        if (state is null)
        {
            MessageBox.Show("Không tìm thấy file cấu hình đã lưu.", "Tải lại cấu hình", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        PrinterConfig = state.PrinterConfig ?? new PrinterConfig();
        PrinterStatus = state.PrinterStatus ?? new PrinterStatus();
        _appStatePrinterCounter = state.PrinterStatus?.PrinterCounter ?? 0;
        PrinterStatus.IsConnected = false;
        PrinterStatus.IsPrinting = false;
        PrinterStatus.LastReceivedRawData = string.Empty;
        PrinterStatus.ReceivedRawDataLog = string.Empty;

        var savedSerialItems = await _printerDataLogService.LoadSerialItemsAsync();
        SerialItems = savedSerialItems is { Count: > 0 }
            ? new ObservableCollection<SerialItem>(savedSerialItems)
            : new ObservableCollection<SerialItem>();

        TotalImportedCount = SerialItems.Count;
        ReindexSerialItems();
        RefreshFilteredSerialItems();
        UpdateDerivedState();
        BindMockPrinterItems();
        _lastSavedAt = state.SavedAt;
        MessageBox.Show("Đã tải lại dữ liệu cấu hình và kết quả từ Data Logs.", "Tải lại cấu hình", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task TestConnectionAsync()
    {
        await ConnectPrinterAsync();
    }

    private async Task ConnectPrinterAsync()
    {
        if (IsConnected || IsConnecting)
        {
            return;
        }

        try
        {
            _isConnecting = true;
            UpdateDerivedState();
            await Task.Delay(300);

            var connected = await _printerService.ConnectAsync(PrinterConfig);
            if (connected)
            {
                await RefreshPrinterStatusAsync();
                PrinterStatus.IsConnected = true;
                PrinterStatus.IsPrinting = false;
                PrinterStatus.LastError = string.Empty;
                PrinterStatus.LastUpdatedAt = DateTime.Now;
                _appStatePrinterCounter = PrinterStatus.PrinterCounter;
                OnPropertyChanged(nameof(PrintedCount));
            }
            else
            {
                PrinterStatus.LastError = string.IsNullOrWhiteSpace(_printerService.LastError)
                    ? "Mất kết nối"
                    : _printerService.LastError;
            }
        }
        finally
        {
            _isConnecting = false;
            UpdateDerivedState();
            await SaveStateAsync();
        }
    }

    private async Task DisconnectPrinterAsync()
    {
        await _printerService.DisconnectAsync();
        PrinterStatus.IsConnected = false;
        PrinterStatus.IsPrinting = false;
        PrinterStatus.LastUpdatedAt = DateTime.Now;
        UpdateDerivedState();
        await SaveStateAsync();
    }

    private async Task StartPrintAsync()
    {
        if (!IsConnected)
        {
            return;
        }

        if (await _printerService.StartPrintAsync())
        {
            PrinterStatus.IsPrinting = true;
            PrinterStatus.LastError = string.Empty;
            PrinterStatus.LastUpdatedAt = DateTime.Now;
        }

        UpdateDerivedState();
        await SaveStateAsync();
    }

    private async Task StopPrintAsync()
    {
        if (!IsConnected)
        {
            return;
        }

        if (await _printerService.StopPrintAsync())
        {
            PrinterStatus.IsPrinting = false;
            PrinterStatus.LastUpdatedAt = DateTime.Now;
        }

        UpdateDerivedState();
        await SaveStateAsync();
    }

    private async Task SendBufferAsync()
    {
        await SendNextRemoteFieldDataAsync("Gửi Buffer", showNoDataMessage: true);
    }

    private async Task Send1RemoteFieldDataAsync()
    {
        await SendNextRemoteFieldDataAsync("Gửi 1 Remote Field", showNoDataMessage: true);
    }

    private async Task Send30RemoteFieldsThenStartPrintAsync()
    {
        if (!IsConnected)
        {
            MessageBox.Show("Chưa kết nối máy in.", "Gửi 30 rồi Start Print", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (WaitingCount < 30)
        {
            MessageBox.Show("Cần tối thiểu 30 serial chờ để thực hiện thao tác này.", "Gửi 30 rồi Start Print", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!await _bulkSendLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            for (var i = 0; i < 30; i++)
            {
                var nextItem = SerialItems.FirstOrDefault(x => x.Status == SerialStatus.Waiting);
                if (nextItem is null)
                {
                    MessageBox.Show("Đã hết dữ liệu trước khi gửi đủ 30 serial.", "Gửi 30 rồi Start Print", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var ok = await _printerService.Send1RemoteFieldDataAsync(nextItem.Serial);
                if (!ok)
                {
                    nextItem.Status = SerialStatus.Error;
                    nextItem.Note = "Tự động gửi 30 serial thất bại";
                    PrinterStatus.LastError = _printerService.LastError;
                    PrinterStatus.LastUpdatedAt = DateTime.Now;
                    RefreshFilteredSerialItems();
                    UpdateDerivedState();
                    await SaveStateAsync();
                    return;
                }

                nextItem.Status = SerialStatus.Sent;
                nextItem.SentAt = DateTime.Now;
                nextItem.Note = string.Empty;

                PrinterStatus.SoftwareCounter += 1;
                PrinterStatus.LastSentSerial = nextItem.Serial;
                PrinterStatus.LastError = string.Empty;
                PrinterStatus.LastUpdatedAt = DateTime.Now;
                RefreshFilteredSerialItems();
                UpdateDerivedState();
                await SaveStateAsync();

                if (i < 29)
                {
                    await Task.Delay(30);
                }
            }

            await Task.Delay(30);
            await StartPrintAsync();
        }
        finally
        {
            _bulkSendLock.Release();
        }
    }

    private async Task ClearDataBufferAsync()
    {
        if (!IsConnected)
        {
            MessageBox.Show("Chưa kết nối máy in.", "Xóa dữ liệu đệm", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (await _printerService.ClearDataBufferAsync())
        {
            PrinterStatus.BufferCount = 0;
            PrinterStatus.LastError = string.Empty;
            PrinterStatus.LastUpdatedAt = DateTime.Now;
            UpdateDerivedState();
            await SaveStateAsync();
            return;
        }

        PrinterStatus.LastError = string.IsNullOrWhiteSpace(_printerService.LastError)
            ? "Xóa dữ liệu đệm thất bại"
            : _printerService.LastError;
        PrinterStatus.LastUpdatedAt = DateTime.Now;
        UpdateDerivedState();
        await SaveStateAsync();
    }

    private async Task HandlePrinterTriggerAsync(string rawData)
    {
        await _remoteFieldSendLock.WaitAsync();
        try
        {
            PrinterStatus.LastReceivedRawData = rawData;
            PrinterStatus.ReceivedRawDataLog = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {rawData}";
            PrinterStatus.LastUpdatedAt = DateTime.Now;

            if (!IsConnected || !IsPrinting)
            {
                return;
            }

            await MarkMostRecentPrintedAsync();
            await SendNextRemoteFieldDataAsync("Nhận trigger 1B-0F", showNoDataMessage: false);
        }
        catch (Exception ex)
        {
            PrinterStatus.LastError = $"Trigger handler error: {ex.Message}";
            PrinterStatus.LastUpdatedAt = DateTime.Now;
            UpdateDerivedState();
        }
        finally
        {
            _remoteFieldSendLock.Release();
        }
    }

    private async Task MarkMostRecentPrintedAsync()
    {
        var item = SerialItems
            .Where(x => x.Status == SerialStatus.Sent && x.PrintedAt is null)
            .OrderBy(x => x.Index)
            .FirstOrDefault();
        if (item is null)
        {
            return;
        }

        item.Status = SerialStatus.Printed;
        item.PrintedAt = DateTime.Now;
        item.Note = string.Empty;
        PrinterStatus.LastPrintedSerial = item.Serial;
        PrinterStatus.LastUpdatedAt = DateTime.Now;
        RefreshFilteredSerialItems();
        UpdateDerivedState();
        await _printerDataLogService.SaveSerialItemsAsync(SerialItems);
    }

    private async Task SyncPrintedItemsToPrinterCounterAsync()
    {
        var targetPrintedCount = Math.Min(PrinterStatus.PrinterCounter, ValidCount);
        if (targetPrintedCount < 0)
        {
            targetPrintedCount = 0;
        }

        var validItems = SerialItems
            .Where(x => x.Status is SerialStatus.Waiting or SerialStatus.Sent or SerialStatus.Printed)
            .OrderBy(x => x.Index)
            .ToList();

        if (validItems.Count == 0)
        {
            return;
        }

        var now = DateTime.Now;
        var changed = false;

        for (var i = 0; i < validItems.Count; i++)
        {
            var item = validItems[i];
            if (i < targetPrintedCount)
            {
                if (item.Status != SerialStatus.Printed || item.PrintedAt is null)
                {
                    item.Status = SerialStatus.Printed;
                    item.PrintedAt = now;
                    item.Note = string.Empty;
                    changed = true;
                }
            }
            else if (item.Status == SerialStatus.Printed)
            {
                item.Status = item.SentAt is not null ? SerialStatus.Sent : SerialStatus.Waiting;
                item.PrintedAt = null;
                changed = true;
            }
        }

        if (targetPrintedCount > 0 && targetPrintedCount <= validItems.Count)
        {
            PrinterStatus.LastPrintedSerial = validItems[targetPrintedCount - 1].Serial;
        }
        else if (targetPrintedCount == 0)
        {
            PrinterStatus.LastPrintedSerial = string.Empty;
        }

        PrinterStatus.LastUpdatedAt = now;
        RefreshFilteredSerialItems();
        UpdateDerivedState();

        if (changed)
        {
            await _printerDataLogService.SaveSerialItemsAsync(SerialItems);
        }
    }

    private async Task FinalizeRemainingSentItemsAsync()
    {
        var remainingSentItems = SerialItems
            .Where(x => x.Status == SerialStatus.Sent && x.PrintedAt is null)
            .OrderBy(x => x.Index)
            .ToList();

        if (remainingSentItems.Count == 0)
        {
            return;
        }

        var now = DateTime.Now;
        foreach (var item in remainingSentItems)
        {
            item.Status = SerialStatus.Printed;
            item.PrintedAt = now;
            item.Note = string.Empty;
        }

        PrinterStatus.LastPrintedSerial = remainingSentItems.Last().Serial;
        PrinterStatus.LastUpdatedAt = now;
        RefreshFilteredSerialItems();
        UpdateDerivedState();
        await _printerDataLogService.SaveSerialItemsAsync(SerialItems);
    }

    private async Task SendNextRemoteFieldDataAsync(string actionTitle, bool showNoDataMessage)
    {
        if (!IsConnected)
        {
            if (showNoDataMessage)
            {
                MessageBox.Show("Chưa kết nối máy in.", actionTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return;
        }

        var nextItem = SerialItems.FirstOrDefault(x => x.Status == SerialStatus.Waiting);
        if (nextItem is null)
        {
            if (showNoDataMessage)
            {
                MessageBox.Show("Đã hết dữ liệu in.", actionTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            await SyncPrintedItemsToPrinterCounterAsync();

            if (ShouldStopPrintNow)
            {
                await FinalizeRemainingSentItemsAsync();
                await StopPrintAsync();
            }

            return;
        }

        var ok = await _printerService.Send1RemoteFieldDataAsync(nextItem.Serial);
        if (ok)
        {
            nextItem.Status = SerialStatus.Sent;
            nextItem.SentAt = DateTime.Now;
            nextItem.Note = string.Empty;

            PrinterStatus.SoftwareCounter += 1;
            PrinterStatus.LastSentSerial = nextItem.Serial;
            PrinterStatus.LastError = string.Empty;
            PrinterStatus.LastUpdatedAt = DateTime.Now;
            RefreshFilteredSerialItems();
            UpdateDerivedState();
            await SaveStateAsync();

            return;
        }

        nextItem.Status = SerialStatus.Error;
        nextItem.Note = showNoDataMessage ? "Gửi Remote Field Data thất bại" : "Tự động gửi Remote Field Data thất bại";
        PrinterStatus.LastError = _printerService.LastError;
        PrinterStatus.LastUpdatedAt = DateTime.Now;
        RefreshFilteredSerialItems();
        UpdateDerivedState();
        await SaveStateAsync();
    }

    private async Task GetPrinterStatusAsync()
    {
        await RefreshPrinterStatusAsync();
    }

    private async Task RefreshPrinterStatusAsync()
    {
        if (_isRefreshingStatus)
        {
            return;
        }

        try
        {
            _isRefreshingStatus = true;
            var status = await _printerService.GetStatusAsync();
            PrinterStatus.IsConnected = status.IsConnected;
            PrinterStatus.IsPrinting = status.IsPrinting;
            PrinterStatus.PrinterCounter = status.PrinterCounter;
            _appStatePrinterCounter = status.PrinterCounter;
            OnPropertyChanged(nameof(PrintedCount));
            PrinterStatus.SoftwareCounter = status.SoftwareCounter;
            PrinterStatus.BufferCount = status.BufferCount;
            PrinterStatus.LastSentSerial = status.LastSentSerial;
            PrinterStatus.LastPrintedSerial = status.LastPrintedSerial;
            PrinterStatus.LastReceivedRawData = status.LastReceivedRawData;
            PrinterStatus.ReceivedRawDataLog = status.ReceivedRawDataLog;
            PrinterStatus.LastError = status.LastError;
            PrinterStatus.LastUpdatedAt = status.LastUpdatedAt;
            RefreshFilteredSerialItems();
            UpdateDerivedState();

            await SyncPrintedItemsToPrinterCounterAsync();

            if (ShouldStopPrintNow)
            {
                await FinalizeRemainingSentItemsAsync();
                await StopPrintAsync();
                return;
            }

            await SaveStateAsync();
        }
        finally
        {
            _isRefreshingStatus = false;
        }
    }

    private async Task ResetErrorAsync()
    {
        if (await _printerService.ResetErrorAsync())
        {
            PrinterStatus.LastError = string.Empty;
            PrinterStatus.LastUpdatedAt = DateTime.Now;
        }

        UpdateDerivedState();
        await SaveStateAsync();
    }

    public async Task SaveStateAsync()
    {
        var printerCounterToPersist = IsConnected
            ? PrinterStatus.PrinterCounter
            : _appStatePrinterCounter;

        var state = new AppState
        {
            PrinterConfig = PrinterConfig,
            PrinterStatus = new PrinterStatus
            {
                IsConnected = PrinterStatus.IsConnected,
                IsPrinting = PrinterStatus.IsPrinting,
                PrinterCounter = printerCounterToPersist,
                SoftwareCounter = PrinterStatus.SoftwareCounter,
                BufferCount = PrinterStatus.BufferCount,
                LastSentSerial = PrinterStatus.LastSentSerial,
                LastPrintedSerial = PrinterStatus.LastPrintedSerial,
                LastError = PrinterStatus.LastError,
                LastUpdatedAt = PrinterStatus.LastUpdatedAt
            },
            SavedAt = DateTime.Now
        };

        await _appStateService.SaveAsync(state);
        await _printerDataLogService.SaveSerialItemsAsync(SerialItems);
        _appStatePrinterCounter = printerCounterToPersist;
        OnPropertyChanged(nameof(PrintedCount));
        PrinterStatus.LastUpdatedAt = state.SavedAt;
        _lastSavedAt = state.SavedAt;
        OnPropertyChanged(nameof(LastStateSavedText));
    }

    private void LoadDemoData()
    {
        var demoItems = Enumerable.Range(1, 20)
            .Select(i => new SerialItem
            {
                Index = i,
                Serial = $"SN{i:000000000000000000}",
                Status = SerialStatus.Waiting,
                Note = string.Empty
            })
            .ToList();

        SerialItems = new ObservableCollection<SerialItem>(demoItems);
        TotalImportedCount = demoItems.Count;
        RefreshFilteredSerialItems();
        BindMockPrinterItems();
    }
}























