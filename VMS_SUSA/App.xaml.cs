using System.Windows;
using VMS_SUSA.Services;
using VMS_SUSA.ViewModels;
using VMS_SUSA.Views;

namespace VMS_SUSA;

public partial class App : Application
{
    private MainViewModel? _mainViewModel;

    private async void App_Startup(object sender, StartupEventArgs e)
    {
        var fileDialogService = new FileDialogService();
        var appStateService = new JsonAppStateService();
        var printerService = new MockPrinterService();
        _mainViewModel = new MainViewModel(printerService, appStateService, fileDialogService);

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
