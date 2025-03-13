using System.Text.Json.Serialization;

namespace Presentation_SeedingWall.Models;

/// <summary>
///     聚水潭播种验货请求模型
/// </summary>
public class SeedingVerificationRequest
{
    /// <summary>
    ///     商品SKU码
    /// </summary>
    [JsonPropertyName("skuid")]
    public string SkuId { get; set; } = string.Empty;

    /// <summary>
    ///     播种框号
    /// </summary>
    [JsonPropertyName("index")]
    public string Index { get; set; } = string.Empty;

    /// <summary>
    ///     波次号
    /// </summary>
    [JsonPropertyName("waveid")]
    public string WaveId { get; set; } = string.Empty;
}

/// <summary>
///     聚水潭播种验货响应模型
/// </summary>
public class SeedingVerificationResponse
{
    /// <summary>
    ///     响应状态码
    /// </summary>
    [JsonPropertyName("code")]
    public int Code { get; set; }

    /// <summary>
    ///     响应消息
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    ///     响应数据
    /// </summary>
    [JsonPropertyName("data")]
    public object? Data { get; set; }

    /// <summary>
    ///     是否成功
    /// </summary>
    [JsonIgnore]
    public bool IsSuccess => Code == 200;
}