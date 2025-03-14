using ZtCloudWarehous.Models;

namespace ZtCloudWarehous.Services;

/// <summary>
///     称重服务接口
/// </summary>
internal interface IWeighingService
{
    /// <summary>
    ///     发送称重数据
    /// </summary>
    /// <param name="request">称重请求</param>
    /// <returns>称重响应</returns>
    Task<WeighingResponse> SendWeightDataAsync(WeighingRequest request);
}