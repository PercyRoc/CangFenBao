using Newtonsoft.Json;

namespace ShanghaiModuleBelt.Models.Yunda;

/// <summary>
///     韵达上传重量接口响应
/// </summary>
public class YundaUploadWeightResponse
{
    /// <summary>
    ///     是否成功
    /// </summary>
    [JsonProperty("result")]
    public required bool Result { get; set; }

    /// <summary>
    ///     响应编码
    /// </summary>
    [JsonProperty("code")]
    public required string Code { get; set; }

    /// <summary>
    ///     响应内容
    /// </summary>
    [JsonProperty("message")]
    public string? Message { get; set; }

    /// <summary>
    ///     返回对象信息
    /// </summary>
    [JsonProperty("data")]
    public YundaResponseData? Data { get; set; }
}

/// <summary>
///     韵达响应数据
/// </summary>
public class YundaResponseData
{
    /// <summary>
    ///     错误编码
    /// </summary>
    [JsonProperty("error_code")]
    public string? ErrorCode { get; set; }

    /// <summary>
    ///     错误信息
    /// </summary>
    [JsonProperty("error_msg")]
    public string? ErrorMsg { get; set; }
}