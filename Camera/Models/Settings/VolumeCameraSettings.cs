namespace Camera.Models.Settings;

public class VolumeCameraSettings : BindableBase
{
    private int _fusionTimeMs = 100; // Default value
    private string _imageSavePath = "C:\\VolumeCameraImages"; // 体积相机图像默认保存路径
    private DimensionImageSaveMode _imageSaveMode = DimensionImageSaveMode.None; // 默认不保存尺寸图

    /// <summary>
    /// 获取或设置体积相机图像融合时间（毫秒）。
    /// </summary>
    public int FusionTimeMs
    {
        get => _fusionTimeMs;
        set => SetProperty(ref _fusionTimeMs, value);
    }

    /// <summary>
    /// 获取或设置体积相机图像保存路径。
    /// </summary>
    public string ImageSavePath
    {
        get => _imageSavePath;
        set => SetProperty(ref _imageSavePath, value);
    }

    /// <summary>
    /// 获取或设置尺寸刻度图的保存模式 (使用Flags组合实际保存的视图)。
    /// </summary>
    public DimensionImageSaveMode ImageSaveMode
    {
        get => _imageSaveMode;
        set
        {
            if (SetProperty(ref _imageSaveMode, value))
            {
                RaisePropertyChanged(nameof(SaveVerticalView));
                RaisePropertyChanged(nameof(SaveSideView));
                RaisePropertyChanged(nameof(SaveOriginalView));
            }
        }
    }

    /// <summary>
    /// 获取或设置是否保存俯视图。
    /// </summary>
    public bool SaveVerticalView
    {
        get => (_imageSaveMode & DimensionImageSaveMode.Vertical) == DimensionImageSaveMode.Vertical;
        set
        {
            var currentVal = SaveVerticalView;
            if (currentVal == value) return;

            if (value)
                ImageSaveMode |= DimensionImageSaveMode.Vertical;
            else
                ImageSaveMode &= ~DimensionImageSaveMode.Vertical;
        }
    }

    /// <summary>
    /// 获取或设置是否保存侧视图。
    /// </summary>
    public bool SaveSideView
    {
        get => (_imageSaveMode & DimensionImageSaveMode.Side) == DimensionImageSaveMode.Side;
        set
        {
            var currentVal = SaveSideView;
            if (currentVal == value) return;

            if (value)
                ImageSaveMode |= DimensionImageSaveMode.Side;
            else
                ImageSaveMode &= ~DimensionImageSaveMode.Side;
        }
    }

    /// <summary>
    /// 获取或设置是否保存原图。
    /// </summary>
    public bool SaveOriginalView
    {
        get => (_imageSaveMode & DimensionImageSaveMode.Original) == DimensionImageSaveMode.Original;
        set
        {
            var currentVal = SaveOriginalView;
            if (currentVal == value) return;

            if (value)
                ImageSaveMode |= DimensionImageSaveMode.Original;
            else
                ImageSaveMode &= ~DimensionImageSaveMode.Original;
        }
    }
} 