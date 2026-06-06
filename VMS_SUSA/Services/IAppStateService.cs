using VMS_SUSA.Models;

namespace VMS_SUSA.Services;

public interface IAppStateService
{
    Task SaveAsync(AppState state);
    Task<AppState?> LoadAsync();
}
