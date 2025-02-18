using System.Collections.ObjectModel;
using CommonLibrary.Models.Settings.Camera;
using CommonLibrary.Models.Settings.Camera.Enums;
using CommonLibrary.Services;
using DeviceService.Camera;
using Presentation_CommonLibrary.Services;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;

namespace Presentation_BenFly.ViewModels.Settings;

public class CameraSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;
    private readonly CameraFactory _cameraFactory;
    private readonly INotificationService _notificationService;

    private bool _barcodeRepeatFilterEnabled;

    private bool _isRefreshing;

    private int _repeatCount;

    private int _repeatTimeMs;

    private CameraType _selectedCameraType;

    private CameraManufacturer _selectedManufacturer;

    public CameraSettingsViewModel(
        ISettingsService settingsService,
        CameraFactory cameraFactory,
        INotificationService notificationService)
    {
        _settingsService = settingsService;
        _cameraFactory = cameraFactory;
        _notificationService = notificationService;

        RefreshCameraListCommand = new DelegateCommand(ExecuteRefreshCameraList);
        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);

        // 加载配置
        LoadSettings();
    }

    public CameraManufacturer SelectedManufacturer
    {
        get => _selectedManufacturer;
        set => SetProperty(ref _selectedManufacturer, value);
    }

    public CameraType SelectedCameraType
    {
        get => _selectedCameraType;
        set => SetProperty(ref _selectedCameraType, value);
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        set => SetProperty(ref _isRefreshing, value);
    }

    public bool BarcodeRepeatFilterEnabled
    {
        get => _barcodeRepeatFilterEnabled;
        set => SetProperty(ref _barcodeRepeatFilterEnabled, value);
    }

    public int RepeatCount
    {
        get => _repeatCount;
        set => SetProperty(ref _repeatCount, value);
    }

    public int RepeatTimeMs
    {
        get => _repeatTimeMs;
        set => SetProperty(ref _repeatTimeMs, value);
    }

    public static Array Manufacturers => Enum.GetValues(typeof(CameraManufacturer));
    public static Array CameraTypes => Enum.GetValues(typeof(CameraType));

    public ObservableCollection<DeviceCameraInfo> AvailableCameras { get; } = [];

    public DelegateCommand RefreshCameraListCommand { get; }
    public DelegateCommand SaveConfigurationCommand { get; }

    private void ExecuteRefreshCameraList()
    {
        IsRefreshing = true;
        try
        {
            Log.Information("开始刷新相机列表，当前选择的厂商：{Manufacturer}", SelectedManufacturer);
            
            // 创建相机服务
            using var cameraService = _cameraFactory.CreateCameraByManufacturer(SelectedManufacturer);
            
            // 获取相机列表
            var cameraInfos = cameraService.GetCameraInfos();
            if (cameraInfos == null)
            {
                _notificationService.ShowWarningWithToken("获取相机列表失败", "SettingWindowGrowl");
                return;
            }

            // 更新相机列表
            AvailableCameras.Clear();
            foreach (var info in cameraInfos)
            {
                info.IsSelected = true; // 默认选中所有相机
                AvailableCameras.Add(info);
            }

            if (AvailableCameras.Count == 0)
            {
                _notificationService.ShowWarningWithToken("未找到可用的相机", "SettingWindowGrowl");
            }
            else
            {
                _notificationService.ShowSuccessWithToken($"已找到 {AvailableCameras.Count} 个相机", "SettingWindowGrowl");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "刷新相机列表失败");
            _notificationService.ShowErrorWithToken($"刷新相机列表失败: {ex.Message}", "SettingWindowGrowl");
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private void ExecuteSaveConfiguration()
    {
        var settings = new CameraSettings
        {
            Manufacturer = SelectedManufacturer,
            CameraType = SelectedCameraType,
            BarcodeRepeatFilterEnabled = BarcodeRepeatFilterEnabled,
            RepeatCount = RepeatCount,
            RepeatTimeMs = RepeatTimeMs,
            SelectedCameras = AvailableCameras.Where(c => c.IsSelected).ToList()
        };

        _settingsService.SaveConfiguration("CameraSettings", settings);
    }

    private void LoadSettings()
    {
        var settings = _settingsService.LoadConfiguration<CameraSettings>();

        SelectedManufacturer = settings.Manufacturer;
        SelectedCameraType = settings.CameraType;
        BarcodeRepeatFilterEnabled = settings.BarcodeRepeatFilterEnabled;
        RepeatCount = settings.RepeatCount;
        RepeatTimeMs = settings.RepeatTimeMs;

        AvailableCameras.Clear();
        foreach (var camera in settings.SelectedCameras) AvailableCameras.Add(camera);
    }
}