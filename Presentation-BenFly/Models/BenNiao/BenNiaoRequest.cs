using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Presentation_BenFly.Models.BenNiao;

/// <summary>
///     笨鸟请求基类
/// </summary>
public class BenNiaoRequest
{
    /// <summary>
    ///     合作者唯一ID号
    /// </summary>
    [JsonPropertyName("appId")]
    public string AppId { get; set; } = string.Empty;

    /// <summary>
    ///     消息摘要
    /// </summary>
    [JsonPropertyName("digest")]
    public string Digest { get; set; } = string.Empty;

    /// <summary>
    ///     时间戳
    /// </summary>
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    /// <summary>
    ///     业务参数
    /// </summary>
    [JsonPropertyName("params")]
    public string Params { get; set; } = string.Empty;
}

/// <summary>
///     笨鸟签名帮助类
/// </summary>
public static class BenNiaoSignHelper
{
    /// <summary>
    ///     创建请求
    /// </summary>
    /// <param name="appId">AppId</param>
    /// <param name="appSecret">AppSecret</param>
    /// <param name="data">业务数据</param>
    public static BenNiaoRequest CreateRequest(string appId, string appSecret, object data)
    {
        var timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var paramsJson = JsonSerializer.Serialize(data);

        var request = new BenNiaoRequest
        {
            AppId = appId,
            Timestamp = timestamp,
            Params = paramsJson,
            Digest = GenerateDigest(appId, paramsJson, timestamp, appSecret)
        };

        return request;
    }

    /// <summary>
    ///     生成签名
    /// </summary>
    private static string GenerateDigest(string appId, string paramsJson, long timestamp, string appSecret)
    {
        var signContent = $"{appId}{paramsJson}{timestamp}{appSecret}";
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(signContent));
        return Convert.ToHexString(hash).ToLower();
    }
}