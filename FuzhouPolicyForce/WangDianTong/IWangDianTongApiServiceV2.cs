namespace FuzhouPolicyForce.WangDianTong;

/// <summary>
///     旺店通API服务接口V2 (符合新文档)
/// </summary>
public interface IWangDianTongApiServiceV2
{
    /// <summary>
    ///     重量回传接口，符合旺店通新文档
    /// </summary>
    /// <param name="request">重量回传请求参数</param>
    /// <returns>重量回传响应结果</returns>
    Task<WeightPushResponseV2> PushWeightAsync(WeightPushRequestV2 request);
}