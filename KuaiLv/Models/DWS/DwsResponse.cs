using System.Text.Json.Serialization;

namespace KuaiLv.Models.DWS;

/// <summary>
///     DWS响应模型，兼容两种可能的格式
/// </summary>
public class DwsResponse
{
    /// <summary>
    ///     状态码或状态标识符。
    ///     可能是数字（如 200, 400, 500）或字符串（如 "SERVER_ERROR"）。
    ///     如果响应格式包含 ResponseCodeValue，则此字段可能为字符串。
    /// </summary>
    [JsonPropertyName("code")]
    public object? Code { get; set; } // 使用 object 接收 int 或 string

    /// <summary>
    ///     错误信息或成功时的补充信息。
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>
    ///     返回数据，类型不固定。
    /// </summary>
    [JsonPropertyName("data")]
    public object? Data { get; set; }

    // --- 新增属性以兼容第二种响应格式 --- 

    /// <summary>
    ///     响应码（字符串形式），例如 "INVALID_FULFILLMENT_DATE_PARCEL"。
    ///     仅在某些响应格式中出现。
    /// </summary>
    [JsonPropertyName("responseCode")]
    public string? ResponseCode { get; set; }

    /// <summary>
    ///     响应码（数值形式），例如 1005。
    ///     这是主要的业务逻辑判断依据。
    /// </summary>
    [JsonPropertyName("responseCodeValue")]
    public int? ResponseCodeValue { get; set; }

    /// <summary>
    ///     明确指示操作是否成功。
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>
    ///     指示请求是否成功。
    ///     优先使用 Success 属性；如果不存在，则尝试将 Code 解析为 200。
    /// </summary>
    [JsonIgnore] // 不参与序列化/反序列化
    public bool IsSuccess => Success || (Code is int intCode && intCode == 200);
}