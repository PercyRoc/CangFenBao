using Common.Models.Package;
using DeviceService.DataSourceDevices.Camera.Models;
using DeviceService.DataSourceDevices.Camera.Models.Camera;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

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
    IObservable<(Image<Rgba32> image, IReadOnlyList<BarcodeLocation> barcodes)> ImageStream { get; }

    /// <summary>
    ///     相机连接状态改变事件
    /// </summary>
    event Action<string, bool>? ConnectionChanged;

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
    ///     获取相机信息列表
    /// </summary>
    /// <returns>相机信息列表</returns>
    IEnumerable<DeviceCameraInfo>? GetCameraInfos();

    /// <summary>
    ///     更新相机配置
    /// </summary>
    /// <param name="config">相机配置</param>
    void UpdateConfiguration(CameraSettings config);
}