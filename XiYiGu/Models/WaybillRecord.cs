using System.Text.Json.Serialization;

namespace XiYiGu.Models;

/// <summary>
///     运单记录
/// </summary>
public class WaybillRecord
{
    /// <summary>
    ///     运单号
    /// </summary>
    [JsonPropertyName("waybillNumber")]
    public string WaybillNumber { get; set; } = string.Empty;

    /// <summary>
    ///     重量
    /// </summary>
    [JsonPropertyName("weight")]
    public float Weight { get; set; }

    /// <summary>
    ///     称重时间
    /// </summary>
    [JsonPropertyName("weightTime")]
    public string WeightTime { get; set; } = string.Empty;

    /// <summary>
    ///     体积
    /// </summary>
    [JsonPropertyName("jtWaybillVolume")]
    public string JtWaybillVolume { get; set; } = string.Empty;

    /// <summary>
    ///     尺寸
    /// </summary>
    [JsonPropertyName("jtWaybillSize")]
    public string JtWaybillSize { get; set; } = string.Empty;

    /// <summary>
    ///     历史重量
    /// </summary>
    [JsonPropertyName("jtHistoryWeight")]
    public float JtHistoryWeight { get; set; }
}