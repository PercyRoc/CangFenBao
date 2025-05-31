using Common.Models.Package;
using Common.Services.Devices; // Keep using Common.Services.Devices for IDevice

namespace Sorting_Car.Services // Change namespace to Sorting_Car.Services
{
    /// <summary>
    /// 小车分拣设备的接口
    /// </summary>
    public interface ICarSortingDevice : IDevice
    {
        /// <summary>
        /// 发送分拣命令给小车设备
        /// </summary>
        /// <param name="package">需要分拣的包裹信息</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>命令是否发送成功</returns>
        bool SendCommandForPackage(PackageInfo package, CancellationToken cancellationToken = default);
    }
} 