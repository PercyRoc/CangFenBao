using Common.Models.Package;
using System.Windows.Media.Imaging;

namespace DeviceService.DataSourceDevices.Camera;

/// <summary>
///     相机服务接口
/// </summary>
public interface ICameraService : IDisposable
{
    /// <summary>
    ///     相机是否已连接
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    ///     包裹信息流
    /// </summary>
    IObservable<PackageInfo> PackageStream { get; }

    /// <summary>
    ///     图像信息流
    /// </summary>
    IObservable<BitmapSource> ImageStream { get; }

    /// <summary>
    ///     相机连接状态改变事件
    /// </summary>
    event Action<string?, bool>? ConnectionChanged;

    /// <summary>
    ///     启动相机服务
    /// </summary>
    /// <returns>是否成功</returns>
    bool Start();

    /// <summary>
    ///     停止相机服务
    /// </summary>
    /// <returns>操作是否成功</returns>
    bool Stop();
}