using Common.Models.Package;
using System.Threading.Tasks;

namespace ZtCloudWarehous.Services;

/// <summary>
///     运单上传服务接口
/// </summary>
public interface IWaybillUploadService : IDisposable
{
    /// <summary>
    ///     添加包裹到后台上传队列（不等待完成）
    /// </summary>
    /// <param name="package">包裹信息</param>
    void EnqueuePackage(PackageInfo package);

    /// <summary>
    ///     上传指定包裹并等待其完成。
    /// </summary>
    /// <param name="package">要上传的包裹信息。</param>
    /// <returns>一个表示异步上传操作的任务。</returns>
    Task UploadPackageAndWaitAsync(PackageInfo package);
} 