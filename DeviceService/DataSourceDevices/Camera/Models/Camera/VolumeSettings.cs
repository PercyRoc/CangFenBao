using Common.Services.Settings;

namespace DeviceService.DataSourceDevices.Camera.Models.Camera;

/// <summary>
///     体积相机设置
/// </summary>
[Configuration("VolumeSettings")]
public class VolumeSettings : CameraSettings
{
    /// <summary>
    ///     超时时间（毫秒）
    /// </summary>
    public int TimeoutMs { get; set; } = 5000;
}