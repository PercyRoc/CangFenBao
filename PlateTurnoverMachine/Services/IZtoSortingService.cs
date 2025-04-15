using Common.Models.Package;
using DongtaiFlippingBoardMachine.Models;

namespace DongtaiFlippingBoardMachine.Services;

/// <summary>
/// 中通分拣服务接口
/// </summary>
public interface IZtoSortingService
{
    /// <summary>
    /// 上报流水线开停状态
    /// </summary>
    /// <param name="pipeline">分拣线编码</param>
    /// <param name="status">流水线状态: start | stop | synchronization</param>
    /// <returns>API响应</returns>
    Task<ZtoSortingBaseResponse?> ReportPipelineStatusAsync(string pipeline, string status);
    
    /// <summary>
    /// 获取分拣方案
    /// </summary>
    /// <param name="pipeline">分拣线编码</param>
    /// <returns>分拣方案列表</returns>
    Task<List<object>> GetSortingSettingAsync(string pipeline);
    
    /// <summary>
    /// 获取面单规则
    /// </summary>
    /// <returns>面单规则</returns>
    Task<BillRuleResponse> GetBillRuleAsync();
    
    /// <summary>
    /// 获取分拣信息
    /// </summary>
    /// <param name="billCode">运单编号</param>
    /// <param name="pipeline">分拣线编码</param>
    /// <param name="turnNumber">扫描次数</param>
    /// <param name="trayCode">小车编号</param>
    /// <param name="weight">重量</param>
    /// <returns>分拣信息</returns>
    Task<SortingInfoResponse> GetSortingInfoAsync(string billCode, string pipeline, int turnNumber, string trayCode = "", float? weight = null);
    
    /// <summary>
    /// 推送分拣结果
    /// </summary>
    /// <param name="package">包裹信息</param>
    /// <param name="pipeline">分拣线编码</param>
    /// <param name="turnNumber">扫描次数</param>
    /// <param name="trayCode">小车编号</param>
    /// <returns>分拣结果响应</returns>
    Task<SortingResultResponse> ReportSortingResultAsync(PackageInfo package, string pipeline, int turnNumber, string trayCode = "");
    
    /// <summary>
    /// 校验服务器时间
    /// </summary>
    /// <returns>服务器时间</returns>
    Task<TimeInspectionResponse> InspectTimeAsync();
    
    /// <summary>
    /// 设置服务配置
    /// </summary>
    /// <param name="apiUrl">API地址</param>
    /// <param name="companyId">公司ID</param>
    /// <param name="secretKey">密钥</param>
    void Configure(string apiUrl, string companyId, string secretKey);
} 