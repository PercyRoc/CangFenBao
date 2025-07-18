using System.Text.Json.Serialization;

namespace ZtCloudWarehous.Models;

/// <summary>
///     上传运单记录请求
/// </summary>
public class WaybillUploadRequest
{
    /// <summary>
    ///     设备编号
    /// </summary>
    [JsonPropertyName("machineMx")]
    public string MachineMx { get; init; } = string.Empty;

    /// <summary>
    ///     运单记录列表
    /// </summary>
    [JsonPropertyName("data")]
    public List<WaybillRecord> Data { get; init; } = [];

    /// <summary>
    ///     时间戳（毫秒）
    /// </summary>
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; init; }

    /// <summary>
    ///     签名
    /// </summary>
    [JsonPropertyName("signature")]
    public string Signature { get; set; } = string.Empty;
}