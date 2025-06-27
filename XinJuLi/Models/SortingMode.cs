using System.ComponentModel;

namespace XinJuLi.Models;

/// <summary>
/// 分拣方式枚举
/// </summary>
public enum SortingMode
{
    /// <summary>
    /// 按照大区分拣
    /// </summary>
    [Description("按照大区分拣")]
    AreaCodeSorting = 1,

    /// <summary>
    /// 按照扫码复核结果分拣
    /// </summary>
    [Description("按照扫码复核结果分拣")]
    ScanReviewSorting = 2,

    /// <summary>
    /// 按照ASN单对应分拣
    /// </summary>
    [Description("按照ASN单对应分拣")]
    AsnOrderSorting = 3
}

/// <summary>
/// 摆动方向枚举
/// </summary>
public enum PendulumDirection
{
    /// <summary>
    /// 左摆
    /// </summary>
    [Description("左摆")]
    Left = 1,

    /// <summary>
    /// 右摆
    /// </summary>
    [Description("右摆")]
    Right = 2
} 