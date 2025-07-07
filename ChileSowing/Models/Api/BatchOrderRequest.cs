using System.ComponentModel.DataAnnotations;

namespace ChileSowing.Models.Api;

/// <summary>
/// 分拣单数据同步请求模型
/// </summary>
public class BatchOrderRequest
{
    /// <summary>
    /// 系统编码
    /// </summary>
    [Required]
    public string SystemCode { get; set; } = string.Empty;

    /// <summary>
    /// 仓库编码
    /// </summary>
    [Required]
    public string HouseCode { get; set; } = string.Empty;

    /// <summary>
    /// 分拣单号（订单总号）
    /// </summary>
    [Required]
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>
    /// 执行优先级（1-100，数值越大优先级越高，默认40）
    /// </summary>
    public int Priority { get; set; } = 40;

    /// <summary>
    /// 备注
    /// </summary>
    public string? Remark { get; set; }

    /// <summary>
    /// 订单明细
    /// </summary>
    [Required]
    public List<OrderItem> Items { get; set; } = new();

    /// <summary>
    /// 扩展项
    /// </summary>
    public Dictionary<string, object>? Extra { get; set; }
}

/// <summary>
/// 订单明细项
/// </summary>
public class OrderItem
{
    /// <summary>
    /// 订单明细号
    /// </summary>
    [Required]
    public string DetailCode { get; set; } = string.Empty;

    /// <summary>
    /// 物料条码（二维码、条形码）
    /// </summary>
    [Required]
    public string ItemCode { get; set; } = string.Empty;

    /// <summary>
    /// SKU 代码
    /// </summary>
    public string? SkuCode { get; set; }

    /// <summary>
    /// SKU 名称
    /// </summary>
    public string? SkuName { get; set; }

    /// <summary>
    /// 门店代码
    /// </summary>
    [Required]
    public string ShopCode { get; set; } = string.Empty;

    /// <summary>
    /// 门店名称
    /// </summary>
    public string? ShopName { get; set; }
} 