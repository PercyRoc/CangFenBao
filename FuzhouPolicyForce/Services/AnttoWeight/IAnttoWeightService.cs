using FuzhouPolicyForce.Models.AnttoWeight;

namespace FuzhouPolicyForce.Services.AnttoWeight;

public interface IAnttoWeightService
{
    /// <summary>
    ///     上传称重数据
    /// </summary>
    /// <param name="request">称重请求报文</param>
    /// <returns>称重响应报文</returns>
    Task<AnttoWeightResponse> UploadWeightAsync(AnttoWeightRequest request);
}