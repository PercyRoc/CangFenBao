using CommonLibrary.Services;
using Presentation_CommonLibrary.Services;
using Presentation_KuaiLv.Models.Settings.Upload;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;

namespace Presentation_KuaiLv.ViewModels.Settings;

public class UploadSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;
    private readonly INotificationService _notificationService;
    private UploadConfiguration _configuration = new();

    public UploadSettingsViewModel(
        ISettingsService settingsService,
        INotificationService notificationService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;

        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);

        // 加载配置
        LoadSettings();
    }

    public UploadConfiguration Configuration
    {
        get => _configuration;
        set => SetProperty(ref _configuration, value);
    }

    public static Array Environments => Enum.GetValues(typeof(UploadEnvironment));

    public DelegateCommand SaveConfigurationCommand { get; }

    private void ExecuteSaveConfiguration()
    {
        try
        {
            _settingsService.SaveConfiguration(Configuration);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存回传配置失败");
        }
    }

    private void LoadSettings()
    {
        Configuration = _settingsService.LoadConfiguration<UploadConfiguration>();
    }
} 