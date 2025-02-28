using System.Collections.ObjectModel;
using System.IO;
using CommonLibrary.Models.Settings.Camera;
using CommonLibrary.Models.Settings.Camera.Enums;
using CommonLibrary.Services;
using DeviceService.Camera;
using Microsoft.Win32;
using Presentation_CommonLibrary.Services;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;

namespace Presentation_SangNeng.ViewModels.Settings;

public class CameraSettingsViewModel : BindableBase
{
    private readonly CameraFactory _cameraFactory;
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;
    private CameraSettings? _configuration;

    private bool _isRefreshing;

    public CameraSettingsViewModel(
        ISettingsService settingsService,
        CameraFactory cameraFactory,
        INotificationService notificationService)
    {
        _settingsService = settingsService;
        _cameraFactory = cameraFactory;
        _notificationService = notificationService;

        RefreshCameraListCommand = new DelegateCommand(() =>
        {
            var task = ExecuteRefreshCameraList();
            task.ContinueWith(t =>
            {
                if (!t.IsFaulted || t.Exception == null) return;
                Log.Error(t.Exception, "刷新相机列表时发生错误");
                _notificationService.ShowErrorWithToken("刷新相机列表失败", "SettingWindowGrowl");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        });
        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);
        SelectImagePathCommand = new DelegateCommand(ExecuteSelectImagePath);

        // Load configuration
        LoadSettings();
    }

    private CameraSettings? Configuration
    {
        get => _configuration;
        set => SetProperty(ref _configuration, value);
    }

    public CameraManufacturer SelectedManufacturer
    {
        get => Configuration?.Manufacturer ?? CameraManufacturer.Hikvision;
        set
        {
            if (Configuration == null) return;
            Configuration.Manufacturer = value;
            RaisePropertyChanged();
        }
    }

    public CameraType SelectedCameraType
    {
        get => Configuration?.CameraType ?? CameraType.Smart;
        set
        {
            if (Configuration == null) return;
            Configuration.CameraType = value;
            RaisePropertyChanged();
        }
    }

    public bool BarcodeRepeatFilterEnabled
    {
        get => Configuration?.BarcodeRepeatFilterEnabled ?? false;
        set
        {
            if (Configuration == null) return;
            Configuration.BarcodeRepeatFilterEnabled = value;
            RaisePropertyChanged();
        }
    }

    public int RepeatCount
    {
        get => Configuration?.RepeatCount ?? 3;
        set
        {
            if (Configuration == null) return;
            Configuration.RepeatCount = value;
            RaisePropertyChanged();
        }
    }

    public int RepeatTimeMs
    {
        get => Configuration?.RepeatTimeMs ?? 1000;
        set
        {
            if (Configuration == null) return;
            Configuration.RepeatTimeMs = value;
            RaisePropertyChanged();
        }
    }

    public string ImageSavePath
    {
        get => Configuration?.ImageSavePath ?? "Images";
        set
        {
            if (Configuration == null) return;
            Configuration.ImageSavePath = value;
            RaisePropertyChanged();
        }
    }

    public ImageFormat SelectedImageFormat
    {
        get => Configuration?.ImageFormat ?? ImageFormat.Jpeg;
        set
        {
            if (Configuration == null) return;
            Configuration.ImageFormat = value;
            RaisePropertyChanged();
        }
    }

    public bool EnableImageSaving
    {
        get => Configuration?.EnableImageSaving ?? false;
        set
        {
            if (Configuration == null) return;
            Configuration.EnableImageSaving = value;
            RaisePropertyChanged();
        }
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        set => SetProperty(ref _isRefreshing, value);
    }

    public static Array Manufacturers => Enum.GetValues(typeof(CameraManufacturer));
    public static Array CameraTypes => Enum.GetValues(typeof(CameraType));
    public static Array ImageFormats => Enum.GetValues(typeof(ImageFormat));

    public ObservableCollection<DeviceCameraInfo> AvailableCameras { get; } = [];

    public DelegateCommand RefreshCameraListCommand { get; }
    public DelegateCommand SaveConfigurationCommand { get; }
    public DelegateCommand SelectImagePathCommand { get; }

    private async Task ExecuteRefreshCameraList()
    {
        IsRefreshing = true;
        try
        {
            Log.Information("Starting to refresh camera list, current manufacturer: {Manufacturer}",
                SelectedManufacturer);

            // 先获取相机列表，避免受服务释放影响
            List<DeviceCameraInfo> newCameras;
            await using (var cameraService = _cameraFactory.CreateCameraByManufacturer(SelectedManufacturer))
            {
                var cameraInfos = cameraService.GetCameraInfos();
                if (cameraInfos == null)
                {
                    Log.Warning("Camera service returned null camera list");
                    _notificationService.ShowWarningWithToken("Failed to get camera list", "SettingWindowGrowl");
                    return;
                }

                // 创建相机信息的深拷贝
                newCameras = cameraInfos.Select(info => new DeviceCameraInfo
                {
                    IsSelected = true,
                    Index = info.Index,
                    IpAddress = info.IpAddress,
                    MacAddress = info.MacAddress,
                    SerialNumber = info.SerialNumber,
                    Model = info.Model,
                    Status = info.Status
                }).ToList();
            }

            Log.Information("Retrieved {Count} cameras from service", newCameras.Count);

            // 更新相机列表
            AvailableCameras.Clear();
            foreach (var camera in newCameras)
            {
                Log.Information("Adding camera: IP={IP}, MAC={MAC}, Selected={Selected}",
                    camera.IpAddress, camera.MacAddress, camera.IsSelected);
                AvailableCameras.Add(camera);
            }

            // 更新配置中的相机列表
            if (Configuration != null)
                Configuration.SelectedCameras = new List<DeviceCameraInfo>(
                    AvailableCameras.Select(c => new DeviceCameraInfo(c)));

            if (AvailableCameras.Count == 0)
                _notificationService.ShowWarningWithToken("No available cameras found", "SettingWindowGrowl");
            else
                _notificationService.ShowSuccessWithToken($"Found {AvailableCameras.Count} cameras",
                    "SettingWindowGrowl");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to refresh camera list");
            _notificationService.ShowErrorWithToken($"Failed to refresh camera list: {ex.Message}",
                "SettingWindowGrowl");
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private void ExecuteSelectImagePath()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Select Image Save Path",
            FileName = "Images",
            Filter = "All Files|*.*"
        };

        if (dialog.ShowDialog() == true) ImageSavePath = Path.GetDirectoryName(dialog.FileName) ?? "Images";
    }

    private void ExecuteSaveConfiguration()
    {
        if (Configuration == null) return;
        try
        {
            Log.Information("Starting to save camera configuration...");
            Log.Information("Available cameras count: {Count}", AvailableCameras.Count);

            // 如果当前列表为空但配置中有相机，保持配置中的相机不变
            if (AvailableCameras.Count == 0 && Configuration.SelectedCameras.Count > 0)
            {
                Log.Information("Keeping existing cameras in configuration: {Count}",
                    Configuration.SelectedCameras.Count);
                foreach (var camera in Configuration.SelectedCameras)
                    Log.Information("Existing camera in config: IP={IP}, MAC={MAC}, Selected={Selected}",
                        camera.IpAddress, camera.MacAddress, camera.IsSelected);
            }
            else
            {
                // 否则使用当前列表更新配置
                foreach (var camera in AvailableCameras)
                    Log.Information("Current camera: IP={IP}, MAC={MAC}, Selected={Selected}",
                        camera.IpAddress, camera.MacAddress, camera.IsSelected);

                Configuration.SelectedCameras = AvailableCameras
                    .Where(c => c.IsSelected)
                    .Select(c => new DeviceCameraInfo(c))
                    .ToList();

                Log.Information("Updated selected cameras count: {Count}", Configuration.SelectedCameras.Count);
            }

            _settingsService.SaveConfiguration(Configuration);
            Log.Information("Camera configuration saved successfully");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save camera configuration");
            _notificationService.ShowErrorWithToken("Failed to save camera configuration", "SettingWindowGrowl");
        }
    }

    private void LoadSettings()
    {
        Configuration = _settingsService.LoadConfiguration<CameraSettings>();
        Log.Information("Loading camera settings...");

        // 清空现有列表
        AvailableCameras.Clear();

        if (Configuration?.SelectedCameras == null)
        {
            Log.Information("No saved cameras found in configuration");
            return;
        }

        // 将配置中的相机加载到可用列表中
        Log.Information("Found {Count} cameras in saved configuration", Configuration.SelectedCameras.Count);
        foreach (var camera in Configuration.SelectedCameras)
        {
            Log.Information("Loading camera: IP={IP}, MAC={MAC}, Selected={Selected}",
                camera.IpAddress, camera.MacAddress, camera.IsSelected);

            // 使用深拷贝添加相机
            var newCamera = new DeviceCameraInfo(camera);
            AvailableCameras.Add(newCamera);
        }
    }
}