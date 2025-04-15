using Sunnen.Models;

namespace Sunnen.Services;

public interface ISangNengService
{
    /// <summary>
    ///     发送称重数据到桑能服务器
    /// </summary>
    /// <param name="request">称重请求数据</param>
    /// <returns>服务器响应</returns>
    Task<SangNengWeightResponse> SendWeightDataAsync(SangNengWeightRequest request);
}