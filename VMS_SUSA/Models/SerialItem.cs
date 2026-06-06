using VMS_SUSA.ViewModels;

namespace VMS_SUSA.Models;

public class SerialItem : ViewModelBase
{
    private int _index;
    private string _serial = string.Empty;
    private SerialStatus _status = SerialStatus.Waiting;
    private DateTime? _sentAt;
    private DateTime? _printedAt;
    private string _note = string.Empty;

    public int Index
    {
        get => _index;
        set => SetProperty(ref _index, value);
    }

    public string Serial
    {
        get => _serial;
        set
        {
            if (SetProperty(ref _serial, value))
            {
                OnPropertyChanged(nameof(Length));
            }
        }
    }

    public int Length => Serial?.Length ?? 0;

    public SerialStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public DateTime? SentAt
    {
        get => _sentAt;
        set => SetProperty(ref _sentAt, value);
    }

    public DateTime? PrintedAt
    {
        get => _printedAt;
        set => SetProperty(ref _printedAt, value);
    }

    public string Note
    {
        get => _note;
        set => SetProperty(ref _note, value);
    }

    public string StatusText => Status switch
    {
        SerialStatus.Waiting => "Chờ gửi",
        SerialStatus.Sent => "Đã gửi",
        SerialStatus.Printed => "Đã in",
        SerialStatus.Error => "Lỗi",
        SerialStatus.Duplicate => "Trùng lặp",
        SerialStatus.Invalid => "Sai định dạng",
        _ => string.Empty
    };
}
