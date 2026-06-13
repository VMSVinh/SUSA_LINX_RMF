using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using VMS_SUSA.Models;
using VMS_SUSA.Repositories;
using VMS_SUSA.Services;

namespace VMS_SUSA.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private const int SerialPageSize = 500;
    private const int RemoteFieldBufferTarget = 30;
    private static readonly TimeSpan GridRefreshDelay = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan StateSaveDelay = TimeSpan.FromSeconds(1);

    private readonly IPrinterService _printerService;
    private readonly IAppStateService _appStateService;
    private readonly IPrinterDataLogService _printerDataLogService;
    private readonly ISerialItemRepository _serialItemRepository;
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
    private int _filteredTotalCount;
    private int _currentPage = 1;
    private int _totalPages = 1;
    private SerialItemStatistics _serialStatistics = new(0, 0, 0, 0, 0, 0);
    private int _appStatePrinterCounter;
    private bool _isConnecting;
    private bool _isBusyOperation;
    private bool _isInitialized;
    private bool _isRefreshingStatus;
    private string _operationStatusText = "Sẵn sàng xử lý";
    private readonly SemaphoreSlim _remoteFieldSendLock = new(1, 1);
    private readonly SemaphoreSlim _remoteFieldDataSendLock = new(1, 1);
    private readonly SemaphoreSlim _bulkSendLock = new(1, 1);
    private readonly SemaphoreSlim _bufferTopUpLock = new(1, 1);
    private readonly object _deferredWorkSync = new();
    private bool _resetPrinterCounterAfterImport;
    private bool _isGridRefreshQueued;
    private bool _isStateSaveQueued;
    private bool _isStateSaveRequestedAgain;

    public MainViewModel(IPrinterService printerService, IAppStateService appStateService, IFileDialogService fileDialogService, IPrinterDataLogService printerDataLogService, ISerialItemRepository serialItemRepository)
    {
        _printerService = printerService;
        _appStateService = appStateService;
        _fileDialogService = fileDialogService;
        _printerDataLogService = printerDataLogService;
        _serialItemRepository = serialItemRepository;
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

        BrowseImportFileCommand = new AsyncRelayCommand(BrowseImportFileAsync, () => !IsBusyOperation);
        ImportSerialFileCommand = new AsyncRelayCommand(ImportSerialFileAsync, () => !IsBusyOperation && !string.IsNullOrWhiteSpace(ImportFilePath));
        ClearSerialDataCommand = new AsyncRelayCommand(ClearSerialDataAsync, () => TotalImportedCount > 0 && !IsBusyOperation);
        CheckDuplicateCommand = new AsyncRelayCommand(CheckDuplicateAsync, () => TotalImportedCount > 0 && !IsBusyOperation);
        ExportErrorDataCommand = new AsyncRelayCommand(ExportErrorDataAsync, () => TotalImportedCount > 0 && !IsBusyOperation);

        SaveConfigCommand = new AsyncRelayCommand(SaveConfigAsync);
        ReloadConfigCommand = new AsyncRelayCommand(ReloadConfigAsync);

        ConnectPrinterCommand = new AsyncRelayCommand(ConnectPrinterAsync, () => CanConnect);
        DisconnectPrinterCommand = new AsyncRelayCommand(DisconnectPrinterAsync, () => CanDisconnect);
        StartPrintCommand = new AsyncRelayCommand(StartPrintAsync, () => CanStartPrint);
        StopPrintCommand = new AsyncRelayCommand(StopPrintAsync, () => CanStopPrint);
        Send30RemoteFieldsThenStartPrintCommand = new AsyncRelayCommand(Send30RemoteFieldsThenStartPrintAsync, () => CanSend30RemoteFieldsThenStartPrint);
        ClearDataBufferCommand = new AsyncRelayCommand(ClearDataBufferAsync, () => IsConnected);
        ResetMessageCounterCommand = new AsyncRelayCommand(ResetMessageCounterAsync, () => CanResetMessageCounter);
        GetPrinterStatusCommand = new AsyncRelayCommand(GetPrinterStatusAsync, () => IsConnected);
        ResetErrorCommand = new AsyncRelayCommand(ResetErrorAsync, () => IsConnected || !string.IsNullOrWhiteSpace(PrinterStatus.LastError));
        FirstPageCommand = new RelayCommand(() => GoToPage(1), () => CanGoPreviousPage);
        PreviousPageCommand = new RelayCommand(() => GoToPage(CurrentPage - 1), () => CanGoPreviousPage);
        NextPageCommand = new RelayCommand(() => GoToPage(CurrentPage + 1), () => CanGoNextPage);
        LastPageCommand = new RelayCommand(() => GoToPage(TotalPages), () => CanGoNextPage);

        _relayCommands.AddRange(new[]
        {
            BrowseImportFileCommand,
            ImportSerialFileCommand,
            ClearSerialDataCommand,
            CheckDuplicateCommand,
            ExportErrorDataCommand,
            SaveConfigCommand,
            ReloadConfigCommand,
            ConnectPrinterCommand,
            DisconnectPrinterCommand,
            StartPrintCommand,
            StopPrintCommand,
            Send30RemoteFieldsThenStartPrintCommand,
            ClearDataBufferCommand,
            ResetMessageCounterCommand,
            GetPrinterStatusCommand,
            ResetErrorCommand,
            FirstPageCommand,
            PreviousPageCommand,
            NextPageCommand,
            LastPageCommand
        });

        SerialItems.CollectionChanged += SerialItems_CollectionChanged;
        FilteredSerialItems = new ObservableCollection<SerialItem>();
        _clockTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _clockTimer.Tick += ClockTimer_Tick;

        UpdateCurrentTime();
        UpdatePollingInterval();
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

        PrinterConfig = state?.PrinterConfig ?? new PrinterConfig();
        PrinterStatus = state?.PrinterStatus ?? new PrinterStatus();
        ImportFilePath = state is not null && !string.IsNullOrWhiteSpace(state.ImportFilePath) && File.Exists(state.ImportFilePath)
            ? state.ImportFilePath
            : string.Empty;
        _appStatePrinterCounter = state?.PrinterStatus?.PrinterCounter ?? 0;
        PrinterStatus.IsConnected = false;
        PrinterStatus.IsPrinting = false;
        PrinterStatus.LastReceivedRawData = string.Empty;
        PrinterStatus.ReceivedRawDataLog = string.Empty;

        await RefreshStatisticsFromRepositoryAsync();
        OnPropertyChanged(nameof(PrintedCount));
        if (TotalImportedCount > 0)
        {
            CurrentPage = 1;
            await LoadCurrentPageFromRepositoryAsync();
        }
        else if (state is null)
        {
            await LoadDemoDataAsync();
            UpdateDerivedState();
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
        CurrentPage = 1;
        await LoadCurrentPageFromRepositoryAsync();
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

    public bool IsBusyOperation
    {
        get => _isBusyOperation;
        private set
        {
            if (SetProperty(ref _isBusyOperation, value))
            {
                UpdateCommandStates();
            }
        }
    }

    public string OperationStatusText
    {
        get => _operationStatusText;
        private set => SetProperty(ref _operationStatusText, value);
    }

    public string ImportFilePath
    {
        get => _importFilePath;
        set
        {
            if (SetProperty(ref _importFilePath, value))
            {
                ImportSerialFileCommand.NotifyCanExecuteChanged();
                QueueStateSave();
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
                CurrentPage = 1;
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
                CurrentPage = 1;
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

    public int ValidCount => _serialStatistics.TotalCount - ErrorCount - DuplicateCount - InvalidCount;

    public int DuplicateCount => _serialStatistics.DuplicateCount;

    public int InvalidCount => _serialStatistics.InvalidCount;

    public int SentCount => _serialStatistics.SentCount;

    public int PrintedCount => IsConnected
        ? PrinterStatus.PrinterCounter
        : _appStatePrinterCounter;

    public int ErrorCount => _serialStatistics.ErrorCount;

    public int RemainingCount => _serialStatistics.TotalCount - SentCount - PrintedCount - ErrorCount - DuplicateCount - InvalidCount;

    public int DisplayedCount
    {
        get => _displayedCount;
        private set => SetProperty(ref _displayedCount, value);
    }

    public int FilteredTotalCount
    {
        get => _filteredTotalCount;
        private set => SetProperty(ref _filteredTotalCount, value);
    }

    public int CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (SetProperty(ref _currentPage, value))
            {
                OnPropertyChanged(nameof(PageInfoText));
                OnPropertyChanged(nameof(CanGoPreviousPage));
                OnPropertyChanged(nameof(CanGoNextPage));
                UpdatePagingCommandStates();
            }
        }
    }

    public int TotalPages
    {
        get => _totalPages;
        private set
        {
            if (SetProperty(ref _totalPages, value))
            {
                OnPropertyChanged(nameof(PageInfoText));
                OnPropertyChanged(nameof(CanGoPreviousPage));
                OnPropertyChanged(nameof(CanGoNextPage));
                UpdatePagingCommandStates();
            }
        }
    }

    public string PageInfoText => $"Trang {CurrentPage}/{TotalPages} | Lọc {FilteredTotalCount} | Hiển thị {DisplayedCount}";

    public bool CanConnect => !IsConnected && !IsConnecting;

    public bool CanDisconnect => IsConnected;

    public bool CanStartPrint => IsConnected && !IsConnecting;

    public bool CanStopPrint => IsConnected && IsPrinting;

    public bool CanSend30RemoteFieldsThenStartPrint => IsConnected && ValidCount - PrinterStatus.PrinterCounter >= 30;

    public bool CanResetMessageCounter => IsConnected && !IsConnecting;

    public bool CanEditConfig => !IsPrinting && !IsConnected;

    public bool CanGoPreviousPage => CurrentPage > 1;

    public bool CanGoNextPage => CurrentPage < TotalPages;

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
        ? "ĐANG KẾT NỐI"
        : PrinterStatus.IsConnected
            ? "ĐÃ KẾT NỐI"
            : string.IsNullOrWhiteSpace(PrinterStatus.LastError)
                ? "CHƯA KẾT NỐI"
                : "MẤT KẾT NỐI";

    public string PrintStatusText => PrinterStatus.IsPrinting
        ? "ĐANG IN"
        : string.IsNullOrWhiteSpace(PrinterStatus.LastError)
            ? "DỪNG"
            : "LỖI";

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

    public Brush HeaderBrush => PrinterStatus.IsPrinting
        ? new SolidColorBrush(Color.FromRgb(0xD9, 0x8A, 0x1B))
        : (Brush)new BrushConverter().ConvertFromString("#0F172A")!;

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

    public IRelayCommand ConnectPrinterCommand { get; }
    public IRelayCommand DisconnectPrinterCommand { get; }
    public IRelayCommand StartPrintCommand { get; }
    public IRelayCommand StopPrintCommand { get; }
    public IRelayCommand Send30RemoteFieldsThenStartPrintCommand { get; }
    public IRelayCommand ClearDataBufferCommand { get; }
    public IRelayCommand ResetMessageCounterCommand { get; }
    public IRelayCommand GetPrinterStatusCommand { get; }
    public IRelayCommand ResetErrorCommand { get; }
    public IRelayCommand FirstPageCommand { get; }
    public IRelayCommand PreviousPageCommand { get; }
    public IRelayCommand NextPageCommand { get; }
    public IRelayCommand LastPageCommand { get; }

    private void PrinterConfig_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        UpdateCommandStates();
        UpdateDerivedState();
    }

    private void PrinterStatus_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsPrinting));
        OnPropertyChanged(nameof(PrintedCount));
        OnPropertyChanged(nameof(ConnectionStatusText));
        OnPropertyChanged(nameof(PrintStatusText));
        OnPropertyChanged(nameof(ConnectionStatusBrush));
        OnPropertyChanged(nameof(PrintStatusBrush));
        OnPropertyChanged(nameof(HeaderBrush));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
        OnPropertyChanged(nameof(CanStartPrint));
        OnPropertyChanged(nameof(CanStopPrint));
        OnPropertyChanged(nameof(ClearDataBufferCommand));
        OnPropertyChanged(nameof(ResetMessageCounterCommand));
        OnPropertyChanged(nameof(CanSend30RemoteFieldsThenStartPrint));
        OnPropertyChanged(nameof(CanResetMessageCounter));
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
        _ = HandlePrinterTriggerAsync(e.RawData);
    }

    private void UpdateCurrentTime()
    {
        CurrentTimeText = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");
    }

    private void SetOperationStatus(string text, bool isBusy = true)
    {
        OperationStatusText = text;
        IsBusyOperation = isBusy;
    }

    private void EndOperation(string? finalText = null)
    {
        if (!string.IsNullOrWhiteSpace(finalText))
        {
            OperationStatusText = finalText;
        }

        IsBusyOperation = false;
    }

    private async Task RefreshStatisticsFromRepositoryAsync()
    {
        _serialStatistics = await _serialItemRepository.GetStatisticsAsync().ConfigureAwait(false);
        TotalImportedCount = _serialStatistics.TotalCount;
    }

    private void UpdatePollingInterval()
    {
        var interval = IsConnected && IsPrinting
            ? TimeSpan.FromMilliseconds(300)
            : TimeSpan.FromSeconds(1);

        if (_clockTimer.Interval != interval)
        {
            _clockTimer.Interval = interval;
        }
    }

    private void SetStatisticsSnapshot(SerialItemStatistics statistics)
    {
        _serialStatistics = statistics;
        TotalImportedCount = statistics.TotalCount;
    }

    private void AdjustStatisticsForStatusChange(SerialStatus oldStatus, SerialStatus newStatus)
    {
        if (oldStatus == newStatus)
        {
            return;
        }

        var sent = _serialStatistics.SentCount;
        var printed = _serialStatistics.PrintedCount;
        var error = _serialStatistics.ErrorCount;
        var duplicate = _serialStatistics.DuplicateCount;
        var invalid = _serialStatistics.InvalidCount;

        ApplyDelta(oldStatus, -1, ref sent, ref printed, ref error, ref duplicate, ref invalid);
        ApplyDelta(newStatus, +1, ref sent, ref printed, ref error, ref duplicate, ref invalid);

        _serialStatistics = new SerialItemStatistics(
            _serialStatistics.TotalCount,
            sent,
            printed,
            error,
            duplicate,
            invalid);
    }

    private static void ApplyDelta(
        SerialStatus status,
        int delta,
        ref int sent,
        ref int printed,
        ref int error,
        ref int duplicate,
        ref int invalid)
    {
        switch (status)
        {
            case SerialStatus.Sent:
                sent += delta;
                break;
            case SerialStatus.Printed:
                printed += delta;
                break;
            case SerialStatus.Error:
                error += delta;
                break;
            case SerialStatus.Duplicate:
                duplicate += delta;
                break;
            case SerialStatus.Invalid:
                invalid += delta;
                break;
        }
    }

    private async Task LoadCurrentPageFromRepositoryAsync()
    {
        var page = await _serialItemRepository
            .GetPageAsync(CurrentPage, SerialPageSize, SearchText, SelectedStatusFilter)
            .ConfigureAwait(false);

        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            await dispatcher.InvokeAsync(() => ApplyLoadedPage(page));
        }
        else
        {
            ApplyLoadedPage(page);
        }
    }

    private void ApplyLoadedPage(SerialItemPageResult page)
    {
        FilteredSerialItems.Clear();
        foreach (var item in page.Items)
        {
            FilteredSerialItems.Add(item);
        }

        DisplayedCount = FilteredSerialItems.Count;
        FilteredTotalCount = page.TotalCount;
        TotalPages = page.TotalPages;
        if (CurrentPage != page.PageNumber)
        {
            _currentPage = page.PageNumber;
            OnPropertyChanged(nameof(CurrentPage));
        }

        OnPropertyChanged(nameof(DisplayedCount));
        OnPropertyChanged(nameof(PageInfoText));
    }

    private void UpdateDerivedState()
    {
        UpdatePollingInterval();
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsPrinting));
        OnPropertyChanged(nameof(ValidCount));
        OnPropertyChanged(nameof(DuplicateCount));
        OnPropertyChanged(nameof(InvalidCount));
        OnPropertyChanged(nameof(SentCount));
        OnPropertyChanged(nameof(PrintedCount));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(RemainingCount));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
        OnPropertyChanged(nameof(CanStartPrint));
        OnPropertyChanged(nameof(CanStopPrint));
        OnPropertyChanged(nameof(CanSend30RemoteFieldsThenStartPrint));
        OnPropertyChanged(nameof(CanEditConfig));
        OnPropertyChanged(nameof(ConnectionStatusText));
        OnPropertyChanged(nameof(PrintStatusText));
        OnPropertyChanged(nameof(ConnectionStatusBrush));
        OnPropertyChanged(nameof(PrintStatusBrush));
        OnPropertyChanged(nameof(HeaderBrush));
        OnPropertyChanged(nameof(WarningBrush));
        OnPropertyChanged(nameof(SystemWarningText));
        OnPropertyChanged(nameof(FilteredTotalCount));
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(PageInfoText));
        OnPropertyChanged(nameof(CanGoPreviousPage));
        OnPropertyChanged(nameof(CanGoNextPage));
        OnPropertyChanged(nameof(LastStateSavedText));
        RefreshWarningText();
        UpdateCommandStates();
    }

    private void UpdatePrintProgressState()
    {
        OnPropertyChanged(nameof(ValidCount));
        OnPropertyChanged(nameof(SentCount));
        OnPropertyChanged(nameof(PrintedCount));
        OnPropertyChanged(nameof(RemainingCount));
        OnPropertyChanged(nameof(CanSend30RemoteFieldsThenStartPrint));
        OnPropertyChanged(nameof(PrintStatusText));
        OnPropertyChanged(nameof(PrintStatusBrush));
        OnPropertyChanged(nameof(WarningBrush));
        RefreshWarningText();
        Send30RemoteFieldsThenStartPrintCommand.NotifyCanExecuteChanged();
    }

    private async Task UpdatePrintProgressStateAsync()
    {
        await RunOnUiThreadAsync(UpdatePrintProgressState);
    }

    private Task RunOnUiThreadAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    private Task<T> RunOnUiThreadAsync<T>(Func<T> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return Task.FromResult(action());
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    private Task RunOnUiThreadAsync(Func<Task> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return action();
        }

        return dispatcher.InvokeAsync(action).Task.Unwrap();
    }

    private void QueueGridRefresh()
    {
        if (_isGridRefreshQueued)
        {
            return;
        }

        _isGridRefreshQueued = true;
        _ = RefreshGridAfterDelayAsync();
    }

    private async Task RefreshGridAfterDelayAsync()
    {
        try
        {
            await Task.Delay(GridRefreshDelay).ConfigureAwait(false);
            await LoadCurrentPageFromRepositoryAsync().ConfigureAwait(false);
        }
        finally
        {
            _isGridRefreshQueued = false;
        }
    }

    private void QueueStateSave()
    {
        lock (_deferredWorkSync)
        {
            if (_isStateSaveQueued)
            {
                _isStateSaveRequestedAgain = true;
                return;
            }

            _isStateSaveQueued = true;
        }

        _ = SaveStateAfterDelayAsync();
    }

    private async Task SaveStateAfterDelayAsync()
    {
        try
        {
            await Task.Delay(StateSaveDelay).ConfigureAwait(false);
            await RunOnUiThreadAsync(SaveStateAsync);
        }
        finally
        {
            var saveAgain = false;
            lock (_deferredWorkSync)
            {
                saveAgain = _isStateSaveRequestedAgain;
                _isStateSaveRequestedAgain = false;
                if (!saveAgain)
                {
                    _isStateSaveQueued = false;
                }
            }

            if (saveAgain)
            {
                _ = SaveStateAfterDelayAsync();
            }
        }
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

        if (RemainingCount == 0)
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
        OnPropertyChanged(nameof(ClearDataBufferCommand));
        OnPropertyChanged(nameof(ResetMessageCounterCommand));
        OnPropertyChanged(nameof(CanSend30RemoteFieldsThenStartPrint));
        OnPropertyChanged(nameof(CanResetMessageCounter));
        OnPropertyChanged(nameof(CanEditConfig));
        OnPropertyChanged(nameof(CanGoPreviousPage));
        OnPropertyChanged(nameof(CanGoNextPage));
    }

    private void RefreshFilteredSerialItems()
    {
        _ = LoadCurrentPageFromRepositoryAsync();
    }

    private void UpdatePagingCommandStates()
    {
        FirstPageCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        LastPageCommand.NotifyCanExecuteChanged();
    }

    private void GoToPage(int page)
    {
        var targetPage = Math.Clamp(page, 1, TotalPages);
        if (targetPage == CurrentPage)
        {
            return;
        }

        CurrentPage = targetPage;
        _ = LoadCurrentPageFromRepositoryAsync();
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
        await ExportCurrentGridToCsvAsync();
        var filePath = await _fileDialogService.OpenTextFileAsync();
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            ImportFilePath = filePath;
            await ImportSerialFileAsync();
        }
    }

    private async Task ExportCurrentGridToCsvAsync()
    {
        var searchText = await RunOnUiThreadAsync(() => SearchText);
        var selectedStatusFilter = await RunOnUiThreadAsync(() => SelectedStatusFilter);
        var rows = await _serialItemRepository.GetFilteredAsync(searchText, selectedStatusFilter);
        if (rows.Count == 0)
        {
            return;
        }

        try
        {
            var fileName = $"{DateTime.Now:ss-mm-hh-dd-MM-yyyy}-dataprint.csv";
            var exportPath = Path.Combine(_printerDataLogService.HistoryDataFolderPath, fileName);
            var csv = new StringBuilder();
            csv.AppendLine("STT,Serial,DoDai,TrangThai,ThoiGianGui,ThoiGianIn,GhiChu");

            foreach (var item in rows)
            {
                csv.AppendLine(string.Join(",",
                    EscapeCsv(item.Index.ToString(CultureInfo.InvariantCulture)),
                    EscapeCsv(ExcelTextValue(item.Serial)),
                    EscapeCsv(item.Length.ToString(CultureInfo.InvariantCulture)),
                    EscapeCsv(item.StatusText),
                    EscapeCsv(FormatCsvDate(item.SentAt)),
                    EscapeCsv(FormatCsvDate(item.PrintedAt)),
                    EscapeCsv(item.Note)));
            }

            await File.WriteAllTextAsync(exportPath, csv.ToString(), new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Không thể xuất CSV dữ liệu hiện tại. Chi tiết: {ex.Message}",
                "Xuất CSV",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static string FormatCsvDate(DateTime? value)
    {
        return value.HasValue
            ? value.Value.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture)
            : string.Empty;
    }

    private static string EscapeCsv(string? value)
    {
        var text = value ?? string.Empty;
        var escaped = text.Replace("\"", "\"\"");
        return escaped.Contains(',') || escaped.Contains('"') || escaped.Contains('\n') || escaped.Contains('\r')
            ? $"\"{escaped}\""
            : escaped;
    }

    private static string ExcelTextValue(string? value)
    {
        var text = value ?? string.Empty;
        var escaped = text.Replace("\"", "\"\"");
        return $"=\"{escaped}\"";
    }

    private async Task ImportSerialFileAsync()
    {
        if (string.IsNullOrWhiteSpace(ImportFilePath) || !File.Exists(ImportFilePath))
        {
            MessageBox.Show("Vui lòng chọn file .txt hợp lệ.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetOperationStatus("Đang import dữ liệu...", true);

        var totalLines = 0;
        var importedCount = 0;
        var progress = new Progress<int>(count =>
        {
            if (count % 5000 == 0)
            {
                OperationStatusText = $"Đang import... {count:N0} serial";
            }
        });

        try
        {
            IEnumerable<SerialItem> EnumerateImportedItems()
            {
                foreach (var rawLine in File.ReadLines(ImportFilePath, Encoding.UTF8))
                {
                    totalLines++;

                    var serial = rawLine.Trim();
                    if (string.IsNullOrWhiteSpace(serial))
                    {
                        continue;
                    }

                    importedCount++;
                    var item = new SerialItem
                    {
                        Index = importedCount,
                        Serial = serial,
                        Status = SerialStatus.Waiting,
                        Note = string.Empty
                    };

                    yield return item;
                }
            }

            await _serialItemRepository.ReplaceAllAsync(EnumerateImportedItems(), progress);
            await _serialItemRepository.RecalculateDuplicateStatusesAsync(Math.Max(1, PrinterConfig.SerialLength));
            var statistics = await _serialItemRepository.GetStatisticsAsync();
            SetStatisticsSnapshot(statistics);
            CurrentPage = 1;
            await LoadCurrentPageFromRepositoryAsync();
            await _printerService.ResetSoftwareCounterAsync();
            if (IsConnected && !IsPrinting)
            {
                await _printerService.ResetMessagePrintCountAsync();
                _resetPrinterCounterAfterImport = false;
            }
            else
            {
                _resetPrinterCounterAfterImport = true;
            }

            PrinterStatus.PrinterCounter = 0;
            _appStatePrinterCounter = 0;
            PrinterStatus.SoftwareCounter = 0;
            PrinterStatus.LastSentSerial = string.Empty;
            PrinterStatus.LastPrintedSerial = string.Empty;
            PrinterStatus.LastError = string.Empty;
            PrinterStatus.LastUpdatedAt = DateTime.Now;
            UpdateDerivedState();
            await SaveStateAsync();
            OperationStatusText = $"Đã import {importedCount:N0} serial từ {totalLines:N0} dòng.";
            MessageBox.Show($"Đã import {importedCount} serial từ {totalLines} dòng.", "Import dữ liệu", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task ClearSerialDataAsync()
    {
        ImportFilePath = string.Empty;
        CurrentPage = 1;
        await _serialItemRepository.DeleteAllAsync();
        SetStatisticsSnapshot(new SerialItemStatistics(0, 0, 0, 0, 0, 0));
        await LoadCurrentPageFromRepositoryAsync();
        PrinterStatus.SoftwareCounter = 0;
        PrinterStatus.LastSentSerial = string.Empty;
        PrinterStatus.LastPrintedSerial = string.Empty;
        PrinterStatus.LastError = string.Empty;
        PrinterStatus.LastUpdatedAt = DateTime.Now;
        UpdateDerivedState();
        await SaveStateAsync();
    }

    private async Task CheckDuplicateAsync()
    {
        if (TotalImportedCount == 0)
        {
            MessageBox.Show("Không có dữ liệu để kiểm tra.", "Kiểm tra trùng", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetOperationStatus("Đang kiểm tra trùng bằng SQLite...", true);

        try
        {
            await Task.Yield();
            await _serialItemRepository.RecalculateDuplicateStatusesAsync(Math.Max(1, PrinterConfig.SerialLength));
            var statistics = await _serialItemRepository.GetStatisticsAsync();
            SetStatisticsSnapshot(statistics);
            await LoadCurrentPageFromRepositoryAsync();
            UpdateDerivedState();
            await SaveStateAsync();
            OperationStatusText = "Đã kiểm tra trùng xong.";
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task ExportErrorDataAsync()
    {
        if (TotalImportedCount == 0)
        {
            MessageBox.Show("Không có dữ liệu để xuất.", "Xuất dữ liệu lỗi", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var outputPath = await _fileDialogService.SaveTextFileAsync("serial_errors.txt");
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        var exportedCount = 0;
        await using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await foreach (var item in _serialItemRepository.StreamByStatusesAsync(new[]
        {
            SerialStatus.Invalid,
            SerialStatus.Duplicate,
            SerialStatus.Error
        }))
        {
            await writer.WriteLineAsync($"{item.Index}\t{item.Serial}\t{item.StatusText}\t{item.Note}");
            exportedCount++;
        }

        await writer.FlushAsync();
        MessageBox.Show($"Đã xuất {exportedCount} dòng lỗi.", "Xuất dữ liệu lỗi", MessageBoxButton.OK, MessageBoxImage.Information);
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

        await RefreshStatisticsFromRepositoryAsync();
        CurrentPage = 1;
        await LoadCurrentPageFromRepositoryAsync();
        UpdateDerivedState();
        _lastSavedAt = state.SavedAt;
        MessageBox.Show("Đã tải lại dữ liệu cấu hình và kết quả từ Data Logs.", "Tải lại cấu hình", MessageBoxButton.OK, MessageBoxImage.Information);
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
                var persistedSoftwareCounter = PrinterStatus.SoftwareCounter;
                if (_resetPrinterCounterAfterImport)
                {
                    await _printerService.ResetMessagePrintCountAsync();
                    _resetPrinterCounterAfterImport = false;
                }

                await RefreshPrinterStatusAsync();
                await _printerService.SetSoftwareCounterAsync(persistedSoftwareCounter);
                PrinterStatus.IsConnected = true;
                PrinterStatus.IsPrinting = false;
                PrinterStatus.LastError = string.Empty;
                PrinterStatus.LastUpdatedAt = DateTime.Now;
                _appStatePrinterCounter = PrinterStatus.PrinterCounter;
                OnPropertyChanged(nameof(PrintedCount));
            }
            else
            {
                var connectionError = string.IsNullOrWhiteSpace(_printerService.LastError)
                    ? "Mất kết nối"
                    : _printerService.LastError;
                PrinterStatus.LastError = connectionError;
                MessageBox.Show(connectionError, GetConnectionErrorTitle(connectionError), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            _isConnecting = false;
            UpdateDerivedState();
            await SaveStateAsync();
        }
    }

    private static string GetConnectionErrorTitle(string errorMessage)
    {
        if (errorMessage.Contains("License key", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("MAC", StringComparison.OrdinalIgnoreCase))
        {
            return "Lỗi license";
        }

        return "Lỗi kết nối TCP/IP";
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

    private async Task ClearDataBufferAsync()
    {
        if (!IsConnected)
        {
            MessageBox.Show("Chưa kết nối máy in.", "Xóa dữ liệu đệm", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (await _printerService.ClearDataBufferAsync())
        {
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

    private async Task ResetMessageCounterAsync()
    {
        if (!IsConnected)
        {
            MessageBox.Show("Chưa kết nối máy in.", "Đặt message counter = 0", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!await _printerService.ResetMessagePrintCountAsync())
        {
            PrinterStatus.LastError = string.IsNullOrWhiteSpace(_printerService.LastError)
                ? "Đặt message counter = 0 thất bại"
                : _printerService.LastError;
            PrinterStatus.LastUpdatedAt = DateTime.Now;
            UpdateDerivedState();
            await SaveStateAsync();
            MessageBox.Show(PrinterStatus.LastError, "Đặt message counter = 0", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        PrinterStatus.PrinterCounter = 0;
        _appStatePrinterCounter = 0;
        PrinterStatus.LastError = string.Empty;
        PrinterStatus.LastUpdatedAt = DateTime.Now;
        OnPropertyChanged(nameof(PrintedCount));
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
            await RefreshPrinterStatusAsync();
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
            await RefreshPrinterStatusAsync();
        }

        UpdateDerivedState();
        await SaveStateAsync();
    }

    private async Task Send30RemoteFieldsThenStartPrintAsync()
    {
        if (!IsConnected)
        {
            MessageBox.Show("Chưa kết nối máy in.", "Khởi động", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await RefreshPrinterStatusAsync();

        var startIndex = PrinterStatus.PrinterCounter > 0
            ? PrinterStatus.PrinterCounter
            : 0;
        var availableCount = await _serialItemRepository.CountSendableAfterIndexAsync(startIndex);
        if (availableCount < 30)
        {
            MessageBox.Show(
                availableCount <= 0
                    ? "Không còn đủ dữ liệu để khởi động."
                    : $"Chỉ còn {availableCount} serial hợp lệ sau counter hiện tại, chưa đủ 30.",
                "Khởi động",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!await _bulkSendLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            var batch = await _serialItemRepository.GetSendableAfterIndexAsync(startIndex, 30);
            if (batch.Count < 30)
            {
                MessageBox.Show("Dữ liệu sau counter hiện tại không đủ 30 serial để khởi động.", "Khởi động", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var changed = false;
            for (var i = 0; i < batch.Count; i++)
            {
                var nextItem = batch[i];

                var ok = await _printerService.Send1RemoteFieldDataAsync(nextItem.Serial);
                if (!ok)
                {
                    var oldStatus = nextItem.Status;
                    nextItem.Status = SerialStatus.Error;
                    nextItem.Note = "Tự động gửi 30 serial thất bại";
                    PrinterStatus.LastError = _printerService.LastError;
                    PrinterStatus.LastUpdatedAt = DateTime.Now;
                    await _serialItemRepository.UpdateAsync(nextItem);
                    AdjustStatisticsForStatusChange(oldStatus, nextItem.Status);
                    await LoadCurrentPageFromRepositoryAsync();
                    UpdateDerivedState();
                    await SaveStateAsync();
                    return;
                }

                var previousStatus = nextItem.Status;
                nextItem.Status = SerialStatus.Sent;
                nextItem.SentAt = DateTime.Now;
                nextItem.Note = string.Empty;

                PrinterStatus.SoftwareCounter += 1;
                PrinterStatus.LastSentSerial = nextItem.Serial;
                PrinterStatus.LastError = string.Empty;
                PrinterStatus.LastUpdatedAt = DateTime.Now;
                await _serialItemRepository.UpdateAsync(nextItem);
                AdjustStatisticsForStatusChange(previousStatus, nextItem.Status);
                changed = true;

                if (i < 29)
                {
                    await Task.Delay(30);
                }
            }

            if (changed)
            {
                await LoadCurrentPageFromRepositoryAsync();
                UpdateDerivedState();
                await SaveStateAsync();
            }
        }
        finally
        {
            _bulkSendLock.Release();
        }
    }

    private async Task HandlePrinterTriggerAsync(string rawData)
    {
        await _remoteFieldSendLock.WaitAsync();
        try
        {
            var triggerState = await RunOnUiThreadAsync(() =>
            {
                PrinterStatus.LastReceivedRawData = rawData;
                PrinterStatus.ReceivedRawDataLog = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {rawData}";
                PrinterStatus.LastUpdatedAt = DateTime.Now;

                if (!IsConnected || !IsPrinting)
                {
                    return new TriggerState(false, false);
                }

                PrinterStatus.PrinterCounter += 1;
                _appStatePrinterCounter = PrinterStatus.PrinterCounter;
                return new TriggerState(true, ShouldStopPrintNow);
            });

            if (!triggerState.ShouldProcess)
            {
                return;
            }

            if (triggerState.ShouldStopPrint)
            {
                await FinalizeRemainingSentItemsAsync();
                await RunOnUiThreadAsync(StopPrintAsync);
                return;
            }

            var printedItem = await _serialItemRepository.GetFirstByStatusAsync(SerialStatus.Sent);
            await SendNextRemoteFieldDataAsync("Nhận trigger 1B-0F", showNoDataMessage: false);
            _ = TopUpRemoteFieldBufferAsync();
            await MarkPrintedAsync(printedItem);
        }
        catch (Exception ex)
        {
            await RunOnUiThreadAsync(() =>
            {
                PrinterStatus.LastError = $"Trigger handler error: {ex.Message}";
                PrinterStatus.LastUpdatedAt = DateTime.Now;
                UpdateDerivedState();
            });
        }
        finally
        {
            _remoteFieldSendLock.Release();
        }
    }

    private readonly record struct TriggerState(bool ShouldProcess, bool ShouldStopPrint);

    private async Task MarkMostRecentPrintedAsync()
    {
        var item = await _serialItemRepository.GetFirstByStatusAsync(SerialStatus.Sent);
        await MarkPrintedAsync(item);
    }

    private async Task MarkPrintedAsync(SerialItem? item)
    {
        if (item is null)
        {
            return;
        }

        var previousStatus = item.Status;
        item.Status = SerialStatus.Printed;
        item.PrintedAt = DateTime.Now;
        item.Note = string.Empty;
        await _serialItemRepository.UpdateAsync(item);
        AdjustStatisticsForStatusChange(previousStatus, item.Status);
        await RunOnUiThreadAsync(() =>
        {
            PrinterStatus.LastPrintedSerial = item.Serial;
            PrinterStatus.LastUpdatedAt = DateTime.Now;
            UpdatePrintProgressState();
        });
        QueueGridRefresh();
        QueueStateSave();
    }

    private async Task SyncPrintedItemsToPrinterCounterAsync()
    {
        var printerCounter = await RunOnUiThreadAsync(() => PrinterStatus.PrinterCounter);
        var lastPrintedSerial = await _serialItemRepository.ReconcilePrintedItemsAsync(printerCounter);
        var statistics = await _serialItemRepository.GetStatisticsAsync();
        await RunOnUiThreadAsync(() =>
        {
            SetStatisticsSnapshot(statistics);
            PrinterStatus.LastPrintedSerial = lastPrintedSerial ?? string.Empty;
            UpdatePrintProgressState();
        });
        QueueGridRefresh();
    }

    private async Task FinalizeRemainingSentItemsAsync()
    {
        var now = DateTime.Now;
        var changed = false;

        while (true)
        {
            var item = await _serialItemRepository.GetFirstByStatusAsync(SerialStatus.Sent);
            if (item is null)
            {
                break;
            }

            var previousStatus = item.Status;
            item.Status = SerialStatus.Printed;
            item.PrintedAt = now;
            item.Note = string.Empty;
            await _serialItemRepository.UpdateAsync(item);
            AdjustStatisticsForStatusChange(previousStatus, item.Status);
            changed = true;
        }

        if (changed)
        {
            var lastPrinted = await _serialItemRepository.GetLastByStatusAsync(SerialStatus.Printed);
            await RunOnUiThreadAsync(() =>
            {
                PrinterStatus.LastPrintedSerial = lastPrinted?.Serial ?? string.Empty;
                PrinterStatus.LastUpdatedAt = now;
                UpdatePrintProgressState();
            });
            QueueGridRefresh();
            QueueStateSave();
        }
    }

    private async Task<bool> SendNextRemoteFieldDataAsync(string actionTitle, bool showNoDataMessage)
    {
        await _remoteFieldDataSendLock.WaitAsync();
        try
        {
            var isConnected = await RunOnUiThreadAsync(() => IsConnected);
            if (!isConnected)
            {
                if (showNoDataMessage)
                {
                    await RunOnUiThreadAsync(() =>
                        MessageBox.Show("Chưa kết nối máy in.", actionTitle, MessageBoxButton.OK, MessageBoxImage.Warning));
                }

                return false;
            }

            var nextItem = await _serialItemRepository.GetFirstByStatusAsync(SerialStatus.Waiting);
            if (nextItem is null)
            {
                if (showNoDataMessage)
                {
                    await RunOnUiThreadAsync(() =>
                        MessageBox.Show("Đã hết dữ liệu in.", actionTitle, MessageBoxButton.OK, MessageBoxImage.Warning));
                }

                await SyncPrintedItemsToPrinterCounterAsync();

                return false;
            }

            var ok = await _printerService.Send1RemoteFieldDataAsync(nextItem.Serial);
            if (ok)
            {
                var previousStatus = nextItem.Status;
                nextItem.Status = SerialStatus.Sent;
                nextItem.SentAt = DateTime.Now;
                nextItem.Note = string.Empty;
                await _serialItemRepository.UpdateAsync(nextItem);
                AdjustStatisticsForStatusChange(previousStatus, nextItem.Status);
                await RunOnUiThreadAsync(() =>
                {
                    PrinterStatus.SoftwareCounter += 1;
                    PrinterStatus.LastSentSerial = nextItem.Serial;
                    PrinterStatus.LastError = string.Empty;
                    PrinterStatus.LastUpdatedAt = DateTime.Now;
                    UpdatePrintProgressState();
                });
                QueueGridRefresh();
                QueueStateSave();

                return true;
            }

            var failedPreviousStatus = nextItem.Status;
            nextItem.Status = SerialStatus.Error;
            nextItem.Note = showNoDataMessage ? "Gửi Remote Field Data thất bại" : "Tự động gửi Remote Field Data thất bại";
            await _serialItemRepository.UpdateAsync(nextItem);
            AdjustStatisticsForStatusChange(failedPreviousStatus, nextItem.Status);
            await RunOnUiThreadAsync(() =>
            {
                PrinterStatus.LastError = _printerService.LastError;
                PrinterStatus.LastUpdatedAt = DateTime.Now;
                UpdatePrintProgressState();
            });
            QueueGridRefresh();
            QueueStateSave();
            return false;
        }
        finally
        {
            _remoteFieldDataSendLock.Release();
        }
    }

    private async Task TopUpRemoteFieldBufferAsync()
    {
        if (!await _bufferTopUpLock.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            while (true)
            {
                var bufferAhead = await RunOnUiThreadAsync(() =>
                    PrinterStatus.SoftwareCounter - PrinterStatus.PrinterCounter);

                if (bufferAhead >= RemoteFieldBufferTarget)
                {
                    return;
                }

                var sent = await SendNextRemoteFieldDataAsync("Bù data đệm", showNoDataMessage: false);
                if (!sent)
                {
                    return;
                }
            }
        }
        finally
        {
            _bufferTopUpLock.Release();
        }
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

        if (_remoteFieldSendLock.CurrentCount == 0)
        {
            return;
        }

        try
        {
            _isRefreshingStatus = true;
            var status = await _printerService.GetStatusAsync();

            var previousCounter = PrinterStatus.PrinterCounter;
            var previousConnected = PrinterStatus.IsConnected;
            var previousPrinting = PrinterStatus.IsPrinting;
            var previousSoftwareCounter = PrinterStatus.SoftwareCounter;
            var previousLastSentSerial = PrinterStatus.LastSentSerial;
            var previousLastPrintedSerial = PrinterStatus.LastPrintedSerial;
            var previousLastError = PrinterStatus.LastError;

            PrinterStatus.IsConnected = status.IsConnected;
            PrinterStatus.IsPrinting = status.IsPrinting;
            PrinterStatus.PrinterCounter = status.PrinterCounter;
            _appStatePrinterCounter = status.PrinterCounter;
            PrinterStatus.SoftwareCounter = status.SoftwareCounter;
            PrinterStatus.LastSentSerial = status.LastSentSerial;
            PrinterStatus.LastPrintedSerial = status.LastPrintedSerial;
            PrinterStatus.LastReceivedRawData = status.LastReceivedRawData;
            PrinterStatus.ReceivedRawDataLog = status.ReceivedRawDataLog;
            PrinterStatus.LastError = status.LastError;
            PrinterStatus.LastUpdatedAt = status.LastUpdatedAt;
            UpdateDerivedState();

            var statusChanged =
                previousCounter != status.PrinterCounter ||
                previousConnected != status.IsConnected ||
                previousPrinting != status.IsPrinting ||
                previousSoftwareCounter != status.SoftwareCounter ||
                !string.Equals(previousLastSentSerial, status.LastSentSerial, StringComparison.Ordinal) ||
                !string.Equals(previousLastPrintedSerial, status.LastPrintedSerial, StringComparison.Ordinal) ||
                !string.Equals(previousLastError, status.LastError, StringComparison.Ordinal);

            if (status.PrinterCounter != previousCounter)
            {
                await SyncPrintedItemsToPrinterCounterAsync();
            }

            if (ShouldStopPrintNow)
            {
                await FinalizeRemainingSentItemsAsync();
                await StopPrintAsync();
                return;
            }

            if (statusChanged)
            {
                QueueStateSave();
            }
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
            ImportFilePath = ImportFilePath,
            PrinterStatus = new PrinterStatus
            {
                IsConnected = PrinterStatus.IsConnected,
                IsPrinting = PrinterStatus.IsPrinting,
                PrinterCounter = printerCounterToPersist,
                SoftwareCounter = PrinterStatus.SoftwareCounter,
                LastSentSerial = PrinterStatus.LastSentSerial,
                LastPrintedSerial = PrinterStatus.LastPrintedSerial,
                LastError = PrinterStatus.LastError,
                LastUpdatedAt = PrinterStatus.LastUpdatedAt
            },
            SavedAt = DateTime.Now
        };

        await _appStateService.SaveAsync(state);
        _appStatePrinterCounter = printerCounterToPersist;
        OnPropertyChanged(nameof(PrintedCount));
        PrinterStatus.LastUpdatedAt = state.SavedAt;
        _lastSavedAt = state.SavedAt;
        OnPropertyChanged(nameof(LastStateSavedText));
    }

    private async Task LoadDemoDataAsync()
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

        await _serialItemRepository.ReplaceAllAsync(demoItems).ConfigureAwait(false);
        CurrentPage = 1;
        SetStatisticsSnapshot(new SerialItemStatistics(20, 0, 0, 0, 0, 0));
        await LoadCurrentPageFromRepositoryAsync();
    }
}























