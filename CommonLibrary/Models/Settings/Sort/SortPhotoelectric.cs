using Prism.Mvvm;

namespace CommonLibrary.Models.Settings.Sort;

public class SortPhotoelectric : BindableBase
{
    private string _ipAddress = string.Empty;
    private string _name = string.Empty;

    private int _port;

    private int _resetDelay;

    private int _sortingDelay;

    private int _timeRangeLower;

    private int _timeRangeUpper;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

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

    public int SortingDelay
    {
        get => _sortingDelay;
        set => SetProperty(ref _sortingDelay, value);
    }

    public int ResetDelay
    {
        get => _resetDelay;
        set => SetProperty(ref _resetDelay, value);
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