namespace Server.JuShuiTan.Services;
using Server.JuShuiTan.Models;

/// <summary>
///     聚水潭服务接口
/// </summary>
public interface IJuShuiTanService
{
    /// <summary>
    ///     称重发货
    /// </summary>
    /// <param name="request">称重发货请求</param>
    /// <returns>称重发货响应</returns>
    Task<WeightSendResponse> WeightAndSendAsync(WeightSendRequest request);

    /// <summary>
    ///     批量称重发货
    /// </summary>
    /// <param name="requests">称重发货请求列表</param>
    /// <returns>称重发货响应</returns>
    Task<WeightSendResponse> BatchWeightAndSendAsync(IEnumerable<WeightSendRequest> requests);
}