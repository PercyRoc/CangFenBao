using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using Common.Services.Settings;

namespace BalanceSorting.Models;

/// <summary>
///     摆轮分拣配置
/// </summary>
[Configuration("PendulumSortConfig")]
public class PendulumSortConfig : BindableBase
{
    private ObservableCollection<SortPhotoelectric> _sortingPhotoelectrics = [];
    private TriggerPhotoelectric _triggerPhotoelectric = new();
    private int _debounceMilliseconds;

    /// <summary>
    ///     分拣光电全局防抖时间（毫秒），指定时间内多次收到分拣光电信号只处理第一个，其余丢弃
    /// </summary>
    public int DebounceMilliseconds
    {
        get => _debounceMilliseconds;
        set => SetProperty(ref _debounceMilliseconds, value);
    }

    /// <summary>
    ///     触发光电配置
    /// </summary>
    [Required(ErrorMessage = "触发光电配置不能为空")]
    public TriggerPhotoelectric TriggerPhotoelectric
    {
        get => _triggerPhotoelectric;
        set => SetProperty(ref _triggerPhotoelectric, value);
    }

    /// <summary>
    ///     分拣光电配置列表
    /// </summary>
    public ObservableCollection<SortPhotoelectric> SortingPhotoelectrics
    {
        get => _sortingPhotoelectrics;
        set => SetProperty(ref _sortingPhotoelectrics, value);
    }
}

public class TriggerPhotoelectric : BindableBase
{
    private string _ipAddress = string.Empty;
    private int _port;
    private int _resetDelay;
    private int _sortingDelay;
    private int _sortingTimeRangeLower;
    private int _sortingTimeRangeUpper;
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

    public int SortingTimeRangeLower
    {
        get => _sortingTimeRangeLower;
        set => SetProperty(ref _sortingTimeRangeLower, value);
    }

    public int SortingTimeRangeUpper
    {
        get => _sortingTimeRangeUpper;
        set => SetProperty(ref _sortingTimeRangeUpper, value);
    }
}

public class SortPhotoelectric : TriggerPhotoelectric
{
    private string _name = string.Empty;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }
} 