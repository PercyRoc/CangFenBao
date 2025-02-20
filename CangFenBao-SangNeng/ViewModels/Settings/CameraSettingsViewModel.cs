using System.Collections.ObjectModel;
using System.IO;
using CommonLibrary.Models.Settings.Camera;
using CommonLibrary.Models.Settings.Camera.Enums;
using CommonLibrary.Services;
using DeviceService.Camera;
using Presentation_CommonLibrary.Services;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;

namespace CangFenBao_SangNeng.ViewModels.Settings;

public class CameraSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;
    private readonly CameraFactory _cameraFactory;
    private readonly INotificationService _notificationService;
    private CameraSettings? _configuration;

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

    private bool _isRefreshing;
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

    private void ExecuteRefreshCameraList()
    {
        IsRefreshing = true;
        try
        {
            Log.Information("Starting to refresh camera list, current manufacturer: {Manufacturer}", SelectedManufacturer);
            
            // Create camera service
            using var cameraService = _cameraFactory.CreateCameraByManufacturer(SelectedManufacturer);
            
            // Get camera list
            var cameraInfos = cameraService.GetCameraInfos();
            if (cameraInfos == null)
            {
                _notificationService.ShowWarningWithToken("Failed to get camera list", "SettingWindowGrowl");
                return;
            }

            // Update camera list
            AvailableCameras.Clear();
            foreach (var info in cameraInfos)
            {
                info.IsSelected = true; // Select all cameras by default
                AvailableCameras.Add(info);
            }

            if (AvailableCameras.Count == 0)
            {
                _notificationService.ShowWarningWithToken("No available cameras found", "SettingWindowGrowl");
            }
            else
            {
                _notificationService.ShowSuccessWithToken($"Found {AvailableCameras.Count} cameras", "SettingWindowGrowl");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to refresh camera list");
            _notificationService.ShowErrorWithToken($"Failed to refresh camera list: {ex.Message}", "SettingWindowGrowl");
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private void ExecuteSelectImagePath()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Select Image Save Path",
            FileName = "Images",
            Filter = "All Files|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            ImageSavePath = Path.GetDirectoryName(dialog.FileName) ?? "Images";
        }
    }

    private void ExecuteSaveConfiguration()
    {
        if (Configuration == null) return;
        try
        {
            Configuration.SelectedCameras = AvailableCameras.Where(c => c.IsSelected).ToList();
            _settingsService.SaveConfiguration(Configuration);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存相机配置失败");
        }
    }

    private void LoadSettings()
    {
        Configuration = _settingsService.LoadConfiguration<CameraSettings>();
        
        // 更新相机列表
        AvailableCameras.Clear();
        if (Configuration?.SelectedCameras == null) return;
        foreach (var camera in Configuration.SelectedCameras)
        {
            AvailableCameras.Add(camera);
        }
    }
} 