using System.Windows;
using VMS_SUSA.Services;
using VMS_SUSA.Repositories;
using VMS_SUSA.ViewModels;
using VMS_SUSA.Views;

namespace VMS_SUSA;

public partial class App : Application
{
    private MainViewModel? _mainViewModel;
    private SqliteSerialItemRepository? _sqliteSerialItemRepository;

    private async void App_Startup(object sender, StartupEventArgs e)
    {
        var fileDialogService = new FileDialogService();
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
}
