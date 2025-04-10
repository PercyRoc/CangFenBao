using Common.Models.Package;

namespace ZtCloudWarehous.Services;

/// <summary>
///     运单上传服务接口
/// </summary>
public interface IWaybillUploadService : IDisposable
{
    /// <summary>
    ///     添加包裹到上传队列
    /// </summary>
    /// <param name="package">包裹信息</param>
    void EnqueuePackage(PackageInfo package);
} 