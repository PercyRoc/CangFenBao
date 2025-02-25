using CommonLibrary.Models;
using CommonLibrary.Models.Settings.Camera;
using DeviceService.Camera.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DeviceService.Camera;

/// <summary>
///     相机服务接口
/// </summary>
public interface ICameraService : IAsyncDisposable
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
    ///     异步停止相机服务
    /// </summary>
    /// <param name="timeoutMs">超时时间（毫秒）</param>
    /// <returns>操作是否成功</returns>
    Task<bool> StopAsync(int timeoutMs = 3000);

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