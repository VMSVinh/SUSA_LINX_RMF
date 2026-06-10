using System.Windows;
using VMS_SUSA.Models;
using VMS_SUSA.Services;
using VMS_SUSA.Repositories;
using VMS_SUSA.ViewModels;
using VMS_SUSA.Views;

namespace VMS_SUSA;

public partial class App : Application
{
    private const int ClockRollbackToleranceMinutes = 5;

    private MainViewModel? _mainViewModel;
    private SqliteSerialItemRepository? _sqliteSerialItemRepository;
    private JsonLicenseService? _licenseService;
    private LicenseState? _licenseState;

    private async void App_Startup(object sender, StartupEventArgs e)
    {
        var fileDialogService = new FileDialogService();
        _licenseService = new JsonLicenseService();

        if (!await ValidateLicenseAsync())
        {
            Shutdown();
            return;
        }

        var appStateService = new JsonAppStateService();
        _sqliteSerialItemRepository = new SqliteSerialItemRepository();
        await _sqliteSerialItemRepository.InitializeAsync();
        var printerDataLogService = new PrinterDataLogService(_sqliteSerialItemRepository);
        var printerService = new Linx8900PrinterService(printerDataLogService);
        _mainViewModel = new MainViewModel(printerService, appStateService, fileDialogService, printerDataLogService, _sqliteSerialItemRepository);

        var mainWindow = new MainWindow
        {
            DataContext = _mainViewModel
        };

        MainWindow = mainWindow;
        mainWindow.Show();
        await _mainViewModel.InitializeAsync();
    }

    private async void App_Exit(object sender, ExitEventArgs e)
    {
        if (_mainViewModel is not null)
        {
            await _mainViewModel.SaveStateAsync();
        }
    }

    private async Task<bool> ValidateLicenseAsync()
    {
        if (_licenseService is null)
        {
            MessageBox.Show("Không khởi tạo được dịch vụ license.", "License", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        _licenseState = await _licenseService.LoadAsync();
        if (_licenseState is null)
        {
            MessageBox.Show("Không tìm thấy file license.json trong Data Logs.", "License", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        var now = DateTime.Now;

        if (_licenseState.ExpiresAt == default)
        {
            MessageBox.Show("license.json thiếu ngày hết hạn.", "License", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        if (now > _licenseState.ExpiresAt)
        {
            MessageBox.Show(
                $"License đã hết hạn vào {_licenseState.ExpiresAt:dd/MM/yyyy HH:mm:ss}.",
                "License",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        if (_licenseState.LastRunAt != default)
        {
            var rollbackThreshold = _licenseState.LastRunAt.AddMinutes(-ClockRollbackToleranceMinutes);
            if (now < rollbackThreshold)
            {
                MessageBox.Show(
                    "Phát hiện thời gian hệ thống bị lùi so với lần chạy gần nhất.",
                    "License",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }
        }

        _licenseState.LastRunAt = now;
        await _licenseService.SaveAsync(_licenseState);
        return true;
    }
}
