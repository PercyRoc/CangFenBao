using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using Common.Services.Settings;
using Prism.Mvvm;

namespace Common.Models.Settings.Sort.PendulumSort;

/// <summary>
///     摆轮分拣配置
/// </summary>
[Configuration("PendulumSort")]
public class PendulumSortConfig : BindableBase
{
    private int _continuousSortMaxIntervalMs = 2000;
    private int _globalDebounceTime;
    private ObservableCollection<SortPhotoelectric> _sortingPhotoelectrics = [];

    private double _straightThroughTimeout = 20000; // 默认20秒
    private TriggerPhotoelectric _triggerPhotoelectric = new();

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

    /// <summary>
    ///     全局光电防抖时间 (毫秒)，在此时间内重复信号将被忽略
    /// </summary>
    public int GlobalDebounceTime
    {
        get => _globalDebounceTime;
        set => SetProperty(ref _globalDebounceTime, value);
    }

    /// <summary>
    ///     直行包裹的超时时间（毫秒）。
    ///     对于目的地在最后一个摆轮之后的包裹，其超时被视为正常分拣完成。
    ///     此值应足够长，以确保包裹已通过所有物理设备。
    /// </summary>
    public double StraightThroughTimeout
    {
        get => _straightThroughTimeout;
        set => SetProperty(ref _straightThroughTimeout, value);
    }

    /// <summary>
    ///     连续分拣的最大时间间隔（毫秒）
    ///     只有当下一个相同格口的包裹预计在此时间窗口内到达时，才跳过回正
    /// </summary>
    public int ContinuousSortMaxIntervalMs
    {
        get => _continuousSortMaxIntervalMs;
        set => SetProperty(ref _continuousSortMaxIntervalMs, value);
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