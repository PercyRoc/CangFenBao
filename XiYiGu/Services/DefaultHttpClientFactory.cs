using System.Net.Http;

namespace Presentation_XiYiGu.Services;

/// <summary>
///     默认HTTP客户端工厂
/// </summary>
public class DefaultHttpClientFactory : IHttpClientFactory
{
    private readonly HttpClient _httpClient;

    /// <summary>
    ///     构造函数
    /// </summary>
    public DefaultHttpClientFactory()
    {
        _httpClient = new HttpClient();
    }

    /// <summary>
    ///     创建HTTP客户端
    /// </summary>
    /// <param name="name">客户端名称</param>
    /// <returns>HTTP客户端</returns>
    public HttpClient CreateClient(string name)
    {
        return _httpClient;
    }
}