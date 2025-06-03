using System.Threading.Tasks;
using ShanghaiModuleBelt.Models.Sto;

namespace ShanghaiModuleBelt.Services.Sto;

/// <summary>
/// 申通仓客户出库自动揽收接口服务接口
/// </summary>
public interface IStoAutoReceiveService
{
    /// <summary>
    /// 发送自动揽收请求
    /// </summary>
    /// <param name="request">申通自动揽收请求</param>
    /// <returns>申通自动揽收响应</returns>
    Task<StoAutoReceiveResponse?> SendAutoReceiveRequestAsync(StoAutoReceiveRequest request);
} 