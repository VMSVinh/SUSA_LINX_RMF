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
    private bool _isConnecting;
    private bool _isInitialized;
    private bool _isRefreshingStatus;

    public MainViewModel(IPrinterService printerService, IAppStateService appStateService, IFileDialogService fileDialogService)
    {
        _printerService = printerService;
        _appStateService = appStateService;
        _fileDialogService = fileDialogService;

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
        ClearSerialDataCommand = new RelayCommand(ClearSerialData, () => SerialItems.Count > 0);
        CheckDuplicateCommand = new RelayCommand(CheckDuplicate);
        ExportErrorDataCommand = new AsyncRelayCommand(ExportErrorDataAsync, () => SerialItems.Count > 0);

        SaveConfigCommand = new AsyncRelayCommand(SaveConfigAsync);
        ReloadConfigCommand = new AsyncRelayCommand(ReloadConfigAsync);
        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync, () => !IsConnecting);

        ConnectPrinterCommand = new AsyncRelayCommand(ConnectPrinterAsync, () => CanConnect);
        DisconnectPrinterCommand = new AsyncRelayCommand(DisconnectPrinterAsync, () => CanDisconnect);
        StartPrintCommand = new AsyncRelayCommand(StartPrintAsync, () => CanStartPrint);
        StopPrintCommand = new AsyncRelayCommand(StopPrintAsync, () => CanStopPrint);
        SendBufferCommand = new AsyncRelayCommand(SendBufferAsync, () => CanSendBuffer);
        TestRemoteFieldDataCommand = new AsyncRelayCommand(TestRemoteFieldDataAsync, () => CanSendBuffer);
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
            TestRemoteFieldDataCommand,
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
        if (state is null)
        {
            LoadDemoData();
            UpdateDerivedState();
            RefreshFilteredSerialItems();
            BindMockPrinterItems();
            _lastSavedAt = default;
            _clockTimer.Start();
            return;
        }

        PrinterConfig = state.PrinterConfig ?? new PrinterConfig();
        PrinterStatus = state.PrinterStatus ?? new PrinterStatus();

        SerialItems.CollectionChanged -= SerialItems_CollectionChanged;
        SerialItems = new ObservableCollection<SerialItem>(state.SerialItems ?? new List<SerialItem>());
        SerialItems.CollectionChanged += SerialItems_CollectionChanged;

        ReindexSerialItems();
        BindMockPrinterItems();
        UpdateDerivedState();
        RefreshFilteredSerialItems();
        UpdateCurrentTime();
        _lastSavedAt = state.SavedAt;
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

    public int PrintedCount => PrinterStatus.PrinterCounter;

    public int ErrorCount => SerialItems.Count(x => x.Status == SerialStatus.Error);

    public int RemainingCount => WaitingCount;

    public int DisplayedCount
    {
        get => _displayedCount;
        private set => SetProperty(ref _displayedCount, value);
    }

    public bool CanConnect => !IsConnected && !IsConnecting;

    public bool CanDisconnect => IsConnected;

    public bool CanStartPrint => IsConnected && !IsPrinting;

    public bool CanStopPrint => IsConnected && IsPrinting;

    public bool CanSendBuffer => IsConnected && WaitingCount > 0;

    public bool CanEditConfig => !IsPrinting;

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
    public IRelayCommand TestRemoteFieldDataCommand { get; }
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

    private void ClearSerialData()
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
    }

    private void CheckDuplicate()
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
        SerialItems = new ObservableCollection<SerialItem>(state.SerialItems ?? new List<SerialItem>());
        TotalImportedCount = SerialItems.Count;
        ReindexSerialItems();
        RefreshFilteredSerialItems();
        UpdateDerivedState();
        BindMockPrinterItems();
        _lastSavedAt = state.SavedAt;
        MessageBox.Show("Đã tải lại dữ liệu từ file JSON.", "Tải lại cấu hình", MessageBoxButton.OK, MessageBoxImage.Information);
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
                PrinterStatus.LastError = string.Empty;
                PrinterStatus.LastUpdatedAt = DateTime.Now;
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
        if (!CanSendBuffer)
        {
            MessageBox.Show("Đã hết dữ liệu in.", "Gửi Buffer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var bufferItem = SerialItems.FirstOrDefault(x => x.Status == SerialStatus.Waiting);

        if (bufferItem is null)
        {
            MessageBox.Show("Đã hết dữ liệu in.", "Gửi Buffer", MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdateDerivedState();
            return;
        }

        var ok = await _printerService.SendBufferAsync(new[] { bufferItem });
        if (ok)
        {
            bufferItem.Status = SerialStatus.Sent;
            bufferItem.SentAt = DateTime.Now;
            bufferItem.Note = string.Empty;

            PrinterStatus.SoftwareCounter += 1;
            PrinterStatus.LastSentSerial = bufferItem.Serial;
            PrinterStatus.BufferCount = SerialItems.Count(x => x.Status == SerialStatus.Sent);
            PrinterStatus.LastUpdatedAt = DateTime.Now;
            RefreshFilteredSerialItems();
            UpdateDerivedState();
            await SaveStateAsync();
            return;
        }

        bufferItem.Status = SerialStatus.Error;
        bufferItem.Note = "Gửi buffer thất bại";

        PrinterStatus.LastError = "Gửi buffer thất bại";
        PrinterStatus.LastUpdatedAt = DateTime.Now;
        UpdateDerivedState();
        await SaveStateAsync();
    }

    private async Task TestRemoteFieldDataAsync()
    {
        if (!IsConnected)
        {
            MessageBox.Show("Chưa kết nối máy in.", "Test Remote Field Data", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var testItem = SerialItems.FirstOrDefault(x => x.Status == SerialStatus.Waiting);
        if (testItem is null)
        {
            MessageBox.Show("Không có serial nào để test.", "Test Remote Field Data", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var ok = await _printerService.TestRemoteFieldDataAsync(testItem.Serial);
        if (ok)
        {
            testItem.Status = SerialStatus.Sent;
            testItem.SentAt = DateTime.Now;
            testItem.Note = "Test gửi Remote Field Data";

            PrinterStatus.SoftwareCounter += 1;
            PrinterStatus.LastSentSerial = testItem.Serial;
            PrinterStatus.LastError = string.Empty;
            PrinterStatus.LastUpdatedAt = DateTime.Now;
            RefreshFilteredSerialItems();
            UpdateDerivedState();
            await SaveStateAsync();
            return;
        }

        testItem.Status = SerialStatus.Error;
        testItem.Note = "Test gửi Remote Field Data thất bại";
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
        var state = new AppState
        {
            PrinterConfig = PrinterConfig,
            PrinterStatus = PrinterStatus,
            SerialItems = SerialItems.ToList(),
            SavedAt = DateTime.Now
        };

        await _appStateService.SaveAsync(state);
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






