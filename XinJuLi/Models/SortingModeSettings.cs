using Common.Services.Settings;
using System.ComponentModel.DataAnnotations;
using Prism.Mvvm;

namespace XinJuLi.Models;

/// <summary>
/// 分拣模式设置
/// </summary>
[Configuration("SortingModeSettings")]
public class SortingModeSettings : BindableBase
{
    private SortingMode _currentSortingMode = SortingMode.ScanReviewSorting;
    private PendulumDirection _pendulumDirection = PendulumDirection.Left;
    private DateTime _lastUpdated = DateTime.Now;
    private string? _remarks;

    /// <summary>
    /// 当前选择的分拣方式
    /// </summary>
    [Required]
    public SortingMode CurrentSortingMode
    {
        get => _currentSortingMode;
        set => SetProperty(ref _currentSortingMode, value);
    }

    /// <summary>
    /// 摆动方向
    /// </summary>
    [Required]
    public PendulumDirection PendulumDirection
    {
        get => _pendulumDirection;
        set => SetProperty(ref _pendulumDirection, value);
    }

    /// <summary>
    /// 设置更新时间
    /// </summary>
    public DateTime LastUpdated
    {
        get => _lastUpdated;
        set => SetProperty(ref _lastUpdated, value);
    }

    /// <summary>
    /// 备注信息
    /// </summary>
    public string? Remarks
    {
        get => _remarks;
        set => SetProperty(ref _remarks, value);
    }

    // 设备ID配置
    private string _skuSortDeviceId = "SKU_SORT_DEVICE_1";
    private string _scanCheckDeviceId = "SCAN_CHECK_DEVICE_1";
    private string _regionSortDeviceId = "REGION_SORT_DEVICE_1";

    /// <summary>
    /// SKU分拣设备ID
    /// </summary>
    public string SkuSortDeviceId
    {
        get => _skuSortDeviceId;
        set => SetProperty(ref _skuSortDeviceId, value);
    }

    /// <summary>
    /// 扫码复核设备ID
    /// </summary>
    public string ScanCheckDeviceId
    {
        get => _scanCheckDeviceId;
        set => SetProperty(ref _scanCheckDeviceId, value);
    }

    /// <summary>
    /// 大区分拣设备ID
    /// </summary>
    public string RegionSortDeviceId
    {
        get => _regionSortDeviceId;
        set => SetProperty(ref _regionSortDeviceId, value);
    }

    /// <summary>
    /// 获取当前分拣方式的显示名称
    /// </summary>
    /// <returns>分拣方式显示名称</returns>
    public string GetCurrentModeDisplayName()
    {
        return CurrentSortingMode switch
        {
            SortingMode.AreaCodeSorting => "按照大区分拣",
            SortingMode.ScanReviewSorting => "按照扫码复核结果分拣",
            SortingMode.AsnOrderSorting => "按照ASN单对应分拣",
            _ => "未知分拣方式"
        };
    }

    /// <summary>
    /// 获取当前摆动方向的显示名称
    /// </summary>
    /// <returns>摆动方向显示名称</returns>
    public string GetPendulumDirectionDisplayName()
    {
        return PendulumDirection switch
        {
            PendulumDirection.Left => "左摆",
            PendulumDirection.Right => "右摆",
            _ => "未知方向"
        };
    }

    /// <summary>
    /// 获取当前分拣模式对应的设备ID
    /// </summary>
    /// <returns>当前模式的设备ID</returns>
    public string GetCurrentDeviceId()
    {
        return CurrentSortingMode switch
        {
            SortingMode.AsnOrderSorting => SkuSortDeviceId,
            SortingMode.ScanReviewSorting => ScanCheckDeviceId,
            SortingMode.AreaCodeSorting => RegionSortDeviceId,
            _ => "UNKNOWN_DEVICE"
        };
    }

    /// <summary>
    /// 验证设置的有效性
    /// </summary>
    /// <returns>验证结果</returns>
    public bool IsValid()
    {
        return Enum.IsDefined(typeof(SortingMode), CurrentSortingMode) &&
               Enum.IsDefined(typeof(PendulumDirection), PendulumDirection);
    }
} 