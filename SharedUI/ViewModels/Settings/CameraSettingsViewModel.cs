using System.Collections.ObjectModel;
using Common.Services.Settings;
using Common.Services.Ui;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Camera.Models.Camera;
using DeviceService.DataSourceDevices.Camera.Models.Camera.Enums;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;

namespace SharedUI.ViewModels.Settings;

public class CameraSettingsViewModel : BindableBase
{
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;
    private CameraSettings _configuration = new();
    private bool _isRefreshing;

    public CameraSettingsViewModel(
        ISettingsService settingsService,
        INotificationService notificationService)
    {
        _settingsService = settingsService;
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

        // 加载配置
        LoadSettings();
    }

    public CameraSettings Configuration
    {
        get => _configuration;
        private set => SetProperty(ref _configuration, value);
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        set => SetProperty(ref _isRefreshing, value);
    }

    public static Array Manufacturers => Enum.GetValues(typeof(CameraManufacturer));
    public static Array CameraTypes => Enum.GetValues(typeof(CameraType));

    public ObservableCollection<DeviceCameraInfo> AvailableCameras { get; } = [];

    public DelegateCommand RefreshCameraListCommand { get; }
    public DelegateCommand SaveConfigurationCommand { get; }

    private Task ExecuteRefreshCameraList()
    {
        IsRefreshing = true;
        try
        {
            Log.Information("开始刷新相机列表，当前选择的厂商：{Manufacturer}，类型：{Type}", Configuration.Manufacturer,
                Configuration.CameraType);
            // 创建相机服务
            using var cameraService =
                CameraFactory.CreateCameraByManufacturer(Configuration.Manufacturer, Configuration.CameraType);
            // 获取相机列表
            var cameraInfos = cameraService.GetCameraInfos();
            if (cameraInfos == null)
            {
                _notificationService.ShowWarningWithToken("获取相机列表失败", "SettingWindowGrowl");
                return Task.CompletedTask;
            }

            // 更新相机列表
            AvailableCameras.Clear();
            foreach (var info in cameraInfos)
            {
                info.IsSelected = true; // 默认选中所有相机
                AvailableCameras.Add(info);
            }

            if (AvailableCameras.Count == 0)
                _notificationService.ShowWarningWithToken("未找到可用的相机", "SettingWindowGrowl");
            else
                _notificationService.ShowSuccessWithToken($"已找到 {AvailableCameras.Count} 个相机", "SettingWindowGrowl");
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

        return Task.CompletedTask;
    }

    private void ExecuteSaveConfiguration()
    {
        try
        {
            Configuration.SelectedCameras = AvailableCameras.Where(static c => c.IsSelected).ToList();
            var results = _settingsService.SaveSettings(Configuration, true);
            if (results.Length > 0)
            {
                var errorMessage = string.Join(", ", results.Select(static r => r.ErrorMessage));
                _notificationService.ShowErrorWithToken($"保存相机配置失败: {errorMessage}", "SettingWindowGrowl");
                return;
            }

            _notificationService.ShowSuccessWithToken("相机设置已保存", "SettingWindowGrowl");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存相机配置失败");
            _notificationService.ShowErrorWithToken("保存相机配置失败", "SettingWindowGrowl");
        }
    }

    private void LoadSettings()
    {
        // 使用IOptionsSnapshot获取最新配置
        Configuration = _settingsService.LoadSettings<CameraSettings>();
        // 更新相机列表
        AvailableCameras.Clear();
        foreach (var camera in Configuration.SelectedCameras) AvailableCameras.Add(camera);
    }
}