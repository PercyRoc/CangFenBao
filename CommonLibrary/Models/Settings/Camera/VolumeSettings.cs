using CommonLibrary.Models.Settings.Camera.Enums;

namespace CommonLibrary.Models.Settings.Camera;

/// <summary>
///     体积相机设置
/// </summary>
[Configuration("VolumeSettings")]
public class VolumeSettings
{
    /// <summary>
    ///     已选择的相机
    /// </summary>
    public DeviceCameraInfo? SelectedCamera { get; set; }

    /// <summary>
    ///     超时时间（毫秒）
    /// </summary>
    public int TimeoutMs { get; set; } = 5000;
} 