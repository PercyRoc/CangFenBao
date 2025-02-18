using CommonLibrary.Models;
using CommonLibrary.Models.Settings.Camera;
using DeviceService.Camera.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DeviceService.Camera;

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
    ///     包裹信息事件
    /// </summary>
    event Action<PackageInfo>? OnPackageInfo;

    /// <summary>
    ///     图像信息事件
    /// </summary>
    event Action<Image<Rgba32>, IReadOnlyList<DahuaBarcodeLocation>>? OnImageReceived;

    /// <summary>
    ///     相机连接状态改变事件
    /// </summary>
    event Action<string, bool>? OnCameraConnectionChanged;

    /// <summary>
    ///     启动相机服务
    /// </summary>
    /// <returns>是否成功</returns>
    bool Start();

    /// <summary>
    ///     停止相机服务
    /// </summary>
    void Stop();

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