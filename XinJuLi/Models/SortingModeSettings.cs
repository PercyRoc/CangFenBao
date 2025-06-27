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
    /// 验证设置的有效性
    /// </summary>
    /// <returns>验证结果</returns>
    public bool IsValid()
    {
        return Enum.IsDefined(typeof(SortingMode), CurrentSortingMode) &&
               Enum.IsDefined(typeof(PendulumDirection), PendulumDirection);
    }
} 