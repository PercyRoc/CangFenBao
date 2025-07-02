using Modules.Models.Jitu;
using ShanghaiModuleBelt.Models.Jitu;

namespace Modules.Services.Jitu;

public interface IJituService
{
    /// <summary>
    ///     发送极兔OpScan请求
    /// </summary>
    /// <param name="request">请求参数</param>
    /// <returns>响应结果</returns>
    Task<JituOpScanResponse> SendOpScanRequestAsync(JituOpScanRequest request);
}