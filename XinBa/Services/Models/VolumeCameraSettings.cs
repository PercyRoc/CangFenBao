using Common.Services.Settings;

namespace XinBa.Services.Models;

/// <summary>
///     体积相机设置
/// </summary>
[Configuration("VolumeCameraSettings")]
public class VolumeCameraSettings : BindableBase
{
    private string _ipAddress = "192.168.1.100";
    private int _maxFusionTimeMs = 500;
    private int _minFusionTimeMs = 50;
    private int _port = 9001;

    /// <summary>
    ///     IP地址
    /// </summary>
    public string IpAddress
    {
        get => _ipAddress;
        set => SetProperty(ref _ipAddress, value);
    }

    /// <summary>
    ///     端口号
    /// </summary>
    public int Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    /// <summary>
    ///     最小融合时间 (毫秒)
    /// </summary>
    public int MinFusionTimeMs
    {
        get => _minFusionTimeMs;
        set => SetProperty(ref _minFusionTimeMs, value);
    }

    /// <summary>
    ///     最大融合时间 (毫秒)
    /// </summary>
    public int MaxFusionTimeMs
    {
        get => _maxFusionTimeMs;
        set => SetProperty(ref _maxFusionTimeMs, value);
    }
}