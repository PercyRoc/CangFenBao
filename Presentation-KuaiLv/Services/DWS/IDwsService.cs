using CommonLibrary.Models;
using Presentation_KuaiLv.Models.DWS;

namespace Presentation_KuaiLv.Services.DWS;

/// <summary>
///     DWS服务接口
/// </summary>
public interface IDwsService
{
    /// <summary>
    ///     上报包裹信息
    /// </summary>
    /// <param name="package">包裹信息</param>
    /// <returns>上报结果</returns>
    Task<DwsResponse> ReportPackageAsync(PackageInfo package);
}