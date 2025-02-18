using CommonLibrary.Models.Settings.Camera.Enums;

namespace CommonLibrary.Models.Settings.Camera;

/// <summary>
///     相机设置
/// </summary>
[Configuration("CameraSettings")]
public class CameraSettings
{
    /// <summary>
    ///     相机厂商
    /// </summary>
    public CameraManufacturer Manufacturer { get; set; }

    /// <summary>
    ///     相机类型
    /// </summary>
    public CameraType CameraType { get; set; }

    /// <summary>
    ///     是否启用条码重复过滤
    /// </summary>
    public bool BarcodeRepeatFilterEnabled { get; set; }

    /// <summary>
    ///     条码重复次数阈值
    /// </summary>
    public int RepeatCount { get; set; } = 3;

    /// <summary>
    ///     条码重复时间窗口（毫秒）
    /// </summary>
    public int RepeatTimeMs { get; set; } = 1000;

    /// <summary>
    ///     已选择的相机列表
    /// </summary>
    public List<DeviceCameraInfo> SelectedCameras { get; set; } = [];
}