using System.Windows.Media.Imaging;
using Camera.Models;
using Common.Models.Package;

namespace Camera.Interface;

public interface ICameraService: IDisposable
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
    ///     带有相机ID的图像信息流
    /// </summary>
    IObservable<(BitmapSource Image, string CameraId)> ImageStreamWithId { get; }

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

    /// <summary>
    ///     获取所有可用的相机基本信息
    /// </summary>
    /// <returns>相机基本信息列表</returns>
    IEnumerable<CameraInfo> GetAvailableCameras();
}