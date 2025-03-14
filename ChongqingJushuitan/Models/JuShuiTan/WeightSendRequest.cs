using System.Text.Json.Serialization;

namespace ChongqingJushuitan.Models.JuShuiTan;

/// <summary>
/// 聚水潭称重发货请求模型
/// </summary>
public class WeightSendRequest
{
    /// <summary>
    /// 快递单号
    /// </summary>
    [JsonPropertyName("l_id")]
    public string LogisticsId { get; set; } = null!;

    /// <summary>
    /// 重量，kg。传0保存0重量，传-1出库单重量为null
    /// </summary>
    [JsonPropertyName("weight")]
    public decimal Weight { get; set; }

    /// <summary>
    /// 是否是国际运单号：默认为false国内快递
    /// </summary>
    [JsonPropertyName("is_un_lid")]
    public bool IsInternational { get; set; }

    /// <summary>
    /// 默认值为1
    /// 0:验货后称重
    /// 1:验货后称重并发货
    /// 2:无须验货称重
    /// 3:无须验货称重并发货
    /// 4:发货后称重
    /// 5:自动判断称重并发货
    /// </summary>
    [JsonPropertyName("type")]
    public int Type { get; set; } = 5;

    /// <summary>
    /// 体积（单位：立方米）
    /// </summary>
    [JsonPropertyName("f_volume")]
    public decimal? Volume { get; set; }

    /// <summary>
    /// 备注称重源，显示在订单操作日志中
    /// </summary>
    [JsonPropertyName("channel")]
    public string? Channel { get; set; }
} 