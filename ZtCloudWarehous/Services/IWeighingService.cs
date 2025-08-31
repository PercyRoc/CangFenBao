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

    /// <summary>
    ///     发送新称重接口数据
    /// </summary>
    /// <param name="request">新称重请求</param>
    /// <returns>新称重响应</returns>
    Task<NewWeighingResponse> SendNewWeightDataAsync(NewWeighingRequest request);

    /// <summary>
    ///     根据配置自动选择称重接口发送数据
    /// </summary>
    /// <param name="waybillCode">运单号</param>
    /// <param name="weight">重量</param>
    /// <param name="volume">体积（可选，仅旧接口使用）</param>
    /// <returns>包含详细结果的称重响应对象</returns>
    Task<WeighingResult> SendWeightDataAutoAsync(string waybillCode, decimal weight, decimal? volume = null);
}