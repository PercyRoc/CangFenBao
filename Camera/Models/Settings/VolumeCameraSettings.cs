using Prism.Mvvm;

namespace Camera.Models.Settings;

public class VolumeCameraSettings : BindableBase
{
    private int _fusionTimeMs = 100; // Default value

    /// <summary>
    /// 获取或设置体积相机图像融合时间（毫秒）。
    /// </summary>
    public int FusionTimeMs
    {
        get => _fusionTimeMs;
        set => SetProperty(ref _fusionTimeMs, value);
    }
} 