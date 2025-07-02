using System.Text.Json.Serialization;

namespace ZtCloudWarehous.Models;

/// <summary>
///     运单记录
/// </summary>
public class WaybillRecord
{
    /// <summary>
    ///     运单号
    /// </summary>
    [JsonPropertyName("waybillNumber")]
    public string WaybillNumber { get; init; } = string.Empty;

    /// <summary>
    ///     重量
    /// </summary>
    [JsonPropertyName("weight")]
    public double Weight { get; init; }

    /// <summary>
    ///     称重时间
    /// </summary>
    [JsonPropertyName("weightTime")]
    public string WeightTime { get; init; } = string.Empty;

    /// <summary>
    ///     体积
    /// </summary>
    [JsonPropertyName("jtWaybillVolume")]
    public string JtWaybillVolume { get; init; } = string.Empty;

    /// <summary>
    ///     尺寸
    /// </summary>
    [JsonPropertyName("jtWaybillSize")]
    public string JtWaybillSize { get; init; } = string.Empty;

    /// <summary>
    ///     历史重量
    /// </summary>
    [JsonPropertyName("jtHistoryWeight")]
    public double JtHistoryWeight { get; init; }
}