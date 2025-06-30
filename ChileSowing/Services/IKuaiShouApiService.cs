using ChileSowing.Models.KuaiShou;

namespace ChileSowing.Services;

/// <summary>
/// 快手API服务接口
/// </summary>
public interface IKuaiShouApiService
{
    /// <summary>
    /// 提交扫描信息到快手系统
    /// </summary>
    /// <param name="request">扫描信息请求</param>
    /// <returns>扫描信息响应</returns>
    Task<CommitScanMsgResponse?> CommitScanMsgAsync(CommitScanMsgRequest request);

    /// <summary>
    /// 提交扫描信息到快手系统（简化版本）
    /// </summary>
    /// <param name="shipId">单号</param>
    /// <param name="weight">重量</param>
    /// <param name="length">长度</param>
    /// <param name="width">宽度</param>
    /// <param name="height">高度</param>
    /// <param name="volume">体积</param>
    /// <returns>扫描信息响应</returns>
    Task<CommitScanMsgResponse?> CommitScanMsgAsync(
        string shipId, 
        float weight = 0f, 
        string length = "", 
        string width = "", 
        string height = "", 
        string volume = "");

    /// <summary>
    /// 测试连接
    /// </summary>
    /// <returns>是否连接成功</returns>
    Task<bool> TestConnectionAsync();

    /// <summary>
    /// 是否启用快手接口
    /// </summary>
    bool IsEnabled { get; }
} 