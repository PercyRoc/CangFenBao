using Common.Services.Settings;
using Common.Services.Ui;
using DeviceService.DataSourceDevices.Camera.Models.Camera;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;

namespace Sunnen.ViewModels.Settings;

public class VolumeSettingsViewModel : BindableBase
{
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;
    private VolumeSettings? _configuration;

    public VolumeSettingsViewModel(
        ISettingsService settingsService,
        INotificationService notificationService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;

        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);

        // Load configuration
        LoadSettings();
    }

    private VolumeSettings? Configuration
    {
        get => _configuration;
        set => SetProperty(ref _configuration, value);
    }

    public int TimeoutMs
    {
        get => Configuration?.TimeoutMs ?? 5000;
        set
        {
            if (Configuration == null) return;
            Configuration.TimeoutMs = value;
            RaisePropertyChanged();
        }
    }

    public DelegateCommand SaveConfigurationCommand { get; }

    private void ExecuteSaveConfiguration()
    {
        if (Configuration == null) return;

        try
        {
            _settingsService.SaveSettings(Configuration);
            _notificationService.ShowSuccessWithToken("Volume camera settings saved", "SettingWindowGrowl");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存体积相机配置失败");
        }
    }

    private void LoadSettings()
    {
        Configuration = _settingsService.LoadSettings<VolumeSettings>();
    }
}