using System.Text.Json.Serialization;

namespace ZtCloudWarehous.Models;

/// <summary>
///     API响应
/// </summary>
public class ApiResponse
{
    /// <summary>
    ///     状态码
    /// </summary>
    [JsonPropertyName("code")]
    public int Code { get; set; }

    /// <summary>
    ///     消息
    /// </summary>
    [JsonPropertyName("msg")]
    public string Msg { get; set; } = string.Empty;

    /// <summary>
    ///     数据
    /// </summary>
    [JsonPropertyName("data")]
    public object? Data { get; set; }

    /// <summary>
    ///     是否成功
    /// </summary>
    [JsonIgnore]
    public bool IsSuccess => Code == 200;
} 