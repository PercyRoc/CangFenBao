using Newtonsoft.Json;

namespace ShanghaiModuleBelt.Models.Zto;

public class CollectUploadResponse
{
    /// <summary>
    ///     状态
    /// </summary>
    [JsonProperty("status")]
    public bool Status { get; set; }

    /// <summary>
    ///     异常码
    /// </summary>
    [JsonProperty("code")]
    public string? Code { get; set; }

    /// <summary>
    ///     异常信息
    /// </summary>
    [JsonProperty("message")]
    public string? Message { get; set; }
}