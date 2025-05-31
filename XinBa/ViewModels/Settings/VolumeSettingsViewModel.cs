using Common.Services.Settings;
using Common.Services.Ui;
using Serilog;
using XinBa.Services.Models;

namespace XinBa.ViewModels.Settings;

public class VolumeSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;
    private readonly INotificationService _notificationService;

    private VolumeCameraSettings _settings = new(); // 初始化确保非空

    public VolumeSettingsViewModel(ISettingsService settingsService, INotificationService notificationService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;

        SaveConfigurationCommand = new DelegateCommand(SaveSettings);

        LoadSettings();
    }

    // 添加 Settings 属性
    public VolumeCameraSettings Settings
    {
        get => _settings;
        set => SetProperty(ref _settings, value);
    }
    public DelegateCommand SaveConfigurationCommand { get; }

    private void LoadSettings()
    {
        try
        {
            // 直接加载并赋值给 Settings 属性
            Settings = _settingsService.LoadSettings<VolumeCameraSettings>();
        }
        catch (Exception ex)
        {   
            Log.Error(ex, "Failed to load VolumeCameraSettings.");
            _notificationService.ShowError("Failed to load volume camera settings. Defaults will be used.");
            // 加载失败时使用默认构造函数创建的实例
            Settings = new VolumeCameraSettings(); 
        }
    }

    private void SaveSettings()
    {
        try
        {
            // 直接保存 Settings 属性引用的实例
            _settingsService.SaveSettings(Settings); 
            Log.Information("VolumeCameraSettings saved successfully.");
            _notificationService.ShowSuccess("Volume camera settings saved successfully.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save VolumeCameraSettings.");
            _notificationService.ShowError("Failed to save volume camera settings.");
        }
    }
} 