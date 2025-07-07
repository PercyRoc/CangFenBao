using Common.Services.Settings; // For ConfigurationAttribute

namespace Camera.Models.Settings;

/// <summary>
/// 相机模块的总体设置
/// </summary>
[Configuration("CameraSettings")] // Key for saving/loading via SettingsService
public class CameraOverallSettings : BindableBase
{
    private bool _isImageSaveEnabled = true;
    private ImageSaveSettings _imageSave = new();

    private bool _isBarcodeFilterEnabled = true;
    private BarcodeFilterSettings _barcodeFilter = new();

    private bool _isBarcodeDuplicationEnabled = true;
    private BarcodeDuplicationSettings _barcodeDuplication = new();

    private bool _isBarcodeTypeEnabled = true;
    private BarcodeTypeSettings _barcodeType = new();

    private bool _isVolumeCameraEnabled;
    private VolumeCameraSettings _volumeCamera = new();

    private bool _isHikvisionSecurityCameraEnabled;
    private HikvisionSecurityCameraSettings _hikvisionSecurityCamera = new();

    /// <summary>
    /// 是否启用图像保存功能
    /// </summary>
    public bool IsImageSaveEnabled
    {
        get => _isImageSaveEnabled;
        set => SetProperty(ref _isImageSaveEnabled, value);
    }

    /// <summary>
    /// 图像保存相关设置
    /// </summary>
    public ImageSaveSettings ImageSave
    {
        get => _imageSave;
        set => SetProperty(ref _imageSave, value);
    }

    /// <summary>
    /// 是否启用条码过滤功能
    /// </summary>
    public bool IsBarcodeFilterEnabled
    {
        get => _isBarcodeFilterEnabled;
        set => SetProperty(ref _isBarcodeFilterEnabled, value);
    }

    /// <summary>
    /// 条码过滤相关设置
    /// </summary>
    public BarcodeFilterSettings BarcodeFilter
    {
        get => _barcodeFilter;
        set => SetProperty(ref _barcodeFilter, value);
    }

    /// <summary>
    /// 是否启用条码重复检测功能
    /// </summary>
    public bool IsBarcodeDuplicationEnabled
    {
        get => _isBarcodeDuplicationEnabled;
        set => SetProperty(ref _isBarcodeDuplicationEnabled, value);
    }

    /// <summary>
    /// 条码重复检测相关设置
    /// </summary>
    public BarcodeDuplicationSettings BarcodeDuplication
    {
        get => _barcodeDuplication;
        set => SetProperty(ref _barcodeDuplication, value);
    }

    /// <summary>
    /// 是否启用条码类型选择功能
    /// </summary>
    public bool IsBarcodeTypeEnabled
    {
        get => _isBarcodeTypeEnabled;
        set => SetProperty(ref _isBarcodeTypeEnabled, value);
    }

    /// <summary>
    /// 条码类型选择相关设置
    /// </summary>
    public BarcodeTypeSettings BarcodeType
    {
        get => _barcodeType;
        set => SetProperty(ref _barcodeType, value);
    }

    /// <summary>
    /// 获取或设置是否启用体积相机功能。
    /// </summary>
    public bool IsVolumeCameraEnabled
    {
        get => _isVolumeCameraEnabled;
        set => SetProperty(ref _isVolumeCameraEnabled, value);
    }

    /// <summary>
    /// 获取或设置体积相机的具体设置。
    /// </summary>
    public VolumeCameraSettings VolumeCamera
    {
        get => _volumeCamera;
        set => SetProperty(ref _volumeCamera, value);
    }

    /// <summary>
    /// 获取或设置是否启用海康安防相机功能。
    /// </summary>
    public bool IsHikvisionSecurityCameraEnabled
    {
        get => _isHikvisionSecurityCameraEnabled;
        set => SetProperty(ref _isHikvisionSecurityCameraEnabled, value);
    }

    /// <summary>
    /// 获取或设置海康安防相机的具体设置。
    /// </summary>
    public HikvisionSecurityCameraSettings HikvisionSecurityCamera
    {
        get => _hikvisionSecurityCamera;
        set => SetProperty(ref _hikvisionSecurityCamera, value);
    }
} 