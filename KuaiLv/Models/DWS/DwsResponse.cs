using System.Text.Json.Serialization;

namespace KuaiLv.Models.DWS;

/// <summary>
///     DWS响应模型
/// </summary>
public class DwsResponse
{
    /// <summary>
    ///     状态码：
    ///     - 200：成功
    ///     - 400：客户端错误
    ///     - 401：未授权
    ///     - 500：服务端异常
    /// </summary>
    [JsonPropertyName("code")]
    public int Code { get; set; }

    /// <summary>
    ///     错误信息（失败时返回原因）
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>
    ///     返回数据，可能是字符串或布尔值
    /// </summary>
    [JsonPropertyName("data")]
    public object? Data { get; set; }

    /// <summary>
    ///     是否成功
    /// </summary>
    public bool IsSuccess => Code == 200;
}