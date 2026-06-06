using Microsoft.Win32;

namespace VMS_SUSA.Services;

public sealed class FileDialogService : IFileDialogService
{
    public Task<string?> OpenTextFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            Title = "Chọn file serial .txt"
        };

        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FileName : null);
    }

    public Task<string?> SaveTextFileAsync(string defaultFileName)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = defaultFileName,
            Title = "Xuất dữ liệu lỗi"
        };

        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FileName : null);
    }
}
