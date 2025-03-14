using System.Text.Json.Serialization;

namespace Presentation_XiYiGu.Models;

/// <summary>
///     上传运单图片请求
/// </summary>
public class WaybillImageUploadRequest
{
    /// <summary>
    ///     设备编号
    /// </summary>
    [JsonPropertyName("machineMx")]
    public string MachineMx { get; set; } = string.Empty;

    /// <summary>
    ///     运单图片数据列表
    /// </summary>
    [JsonPropertyName("data")]
    public List<WaybillImageData> Data { get; set; } = new();

    /// <summary>
    ///     时间戳（毫秒）
    /// </summary>
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    /// <summary>
    ///     签名
    /// </summary>
    [JsonPropertyName("signature")]
    public string Signature { get; set; } = string.Empty;
}

/// <summary>
///     运单图片数据
/// </summary>
public class WaybillImageData
{
    /// <summary>
    ///     运单号
    /// </summary>
    [JsonPropertyName("waybillNumber")]
    public string WaybillNumber { get; set; } = string.Empty;

    /// <summary>
    ///     称重扫描时间
    /// </summary>
    [JsonPropertyName("weightTime")]
    public string WeightTime { get; set; } = string.Empty;
}