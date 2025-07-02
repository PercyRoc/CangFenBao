using System.Text.Json.Serialization;

namespace BenFly.Models.BenNiao;

/// <summary>
///     笨鸟接口响应基类
/// </summary>
/// <typeparam name="T">响应数据类型</typeparam>
public class BenNiaoResponse<T>
{
    /// <summary>
    ///     状态码，200表示成功，其它为失败
    /// </summary>
    [JsonPropertyName("code")]
    public int Code { get; set; }

    /// <summary>
    ///     错误描述
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>
    ///     响应数据
    /// </summary>
    [JsonPropertyName("result")]
    public T? Result { get; set; }

    /// <summary>
    ///     是否成功
    /// </summary>
    public bool IsSuccess
    {
        get => Code == 200;
    }
}