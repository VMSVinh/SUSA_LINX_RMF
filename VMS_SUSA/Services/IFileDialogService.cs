namespace VMS_SUSA.Services;

public interface IFileDialogService
{
    Task<string?> OpenTextFileAsync();
    Task<string?> SaveTextFileAsync(string defaultFileName);
}
