namespace FuzhouPolicyForce.WangDianTong;

/// <summary>
/// 旺店通API服务接口
/// </summary>
public interface IWangDianTongApiService
{
    /// <summary>
    /// 重量回传
    /// </summary>
    /// <param name="logisticsNo">物流单号</param>
    /// <param name="weight">重量(克)</param>
    /// <param name="isCheckWeight">是否校验重量是否超限</param>
    /// <param name="isCheckTradeStatus">是否判断退款状态</param>
    /// <param name="packagerNo">打包员编号</param>
    /// <returns>物流信息响应结果</returns>
    Task<WeightPushResponse> PushWeightAsync(
        string logisticsNo, 
        decimal weight, 
        bool isCheckWeight = false, 
        bool isCheckTradeStatus = false, 
        string packagerNo = "");
}

/// <summary>
/// 重量回传响应结果
/// </summary>
public class WeightPushResponse
{
    /// <summary>
    /// 错误码，0表示成功，其他表示失败
    /// </summary>
    public int Code { get; set; }
    
    /// <summary>
    /// 错误描述
    /// </summary>
    public string Message { get; set; } = string.Empty;
    
    /// <summary>
    /// 物流名称
    /// </summary>
    public string LogisticsName { get; set; } = string.Empty;
    
    /// <summary>
    /// 物流类型编码
    /// </summary>
    public string LogisticsType { get; set; } = string.Empty;
    
    /// <summary>
    /// 物流编号
    /// </summary>
    public string LogisticsCode { get; set; } = string.Empty;
    
    /// <summary>
    /// 是否成功
    /// </summary>
    public bool IsSuccess => Code == 0;
} 