using System.Collections.ObjectModel;
using CommonLibrary.Models.Settings.Camera;
using CommonLibrary.Services;
using Presentation_CommonLibrary.Services;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;

namespace CangFenBao_SangNeng.ViewModels.Settings;

public class VolumeSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;
    private readonly INotificationService _notificationService;
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

    public VolumeSettings? Configuration
    {
        get => _configuration;
        set => SetProperty(ref _configuration, value);
    }

    public DeviceCameraInfo? SelectedCamera
    {
        get => Configuration?.SelectedCamera;
        set
        {
            if (Configuration == null) return;
            Configuration.SelectedCamera = value;
            RaisePropertyChanged();
        }
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

    public ObservableCollection<DeviceCameraInfo> AvailableCameras { get; } = [];

    public DelegateCommand SaveConfigurationCommand { get; }

    private void ExecuteSaveConfiguration()
    {
        if (Configuration == null) return;
        
        try
        {
            _settingsService.SaveConfiguration(Configuration);
            _notificationService.ShowSuccessWithToken("Volume camera settings saved", "SettingWindowGrowl");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存体积相机配置失败");
        }
    }

    private void LoadSettings()
    {
        Configuration = _settingsService.LoadConfiguration<VolumeSettings>();
    }
} 