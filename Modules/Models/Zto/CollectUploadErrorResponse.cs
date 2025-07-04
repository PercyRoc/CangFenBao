using Newtonsoft.Json;

namespace ShanghaiModuleBelt.Models.Zto;

public class CollectUploadErrorResponse
{
    /// <summary>
    ///     结果
    /// </summary>
    [JsonProperty("result")]
    public object Result { get; set; } = new();

    /// <summary>
    ///     异常信息
    /// </summary>
    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    ///     状态
    /// </summary>
    [JsonProperty("status")]
    public bool Status { get; set; }

    /// <summary>
    ///     状态码
    /// </summary>
    [JsonProperty("statusCode")]
    public string StatusCode { get; set; } = string.Empty;
}