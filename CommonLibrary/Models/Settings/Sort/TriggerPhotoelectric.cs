using Prism.Mvvm;

namespace CommonLibrary.Models.Settings.Sort;

public class TriggerPhotoelectric : BindableBase
{
    private string _ipAddress = string.Empty;

    private int _port;

    private int _timeRangeLower;

    private int _timeRangeUpper;

    public string IpAddress
    {
        get => _ipAddress;
        set => SetProperty(ref _ipAddress, value);
    }

    public int Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    public int TimeRangeLower
    {
        get => _timeRangeLower;
        set => SetProperty(ref _timeRangeLower, value);
    }

    public int TimeRangeUpper
    {
        get => _timeRangeUpper;
        set => SetProperty(ref _timeRangeUpper, value);
    }
}