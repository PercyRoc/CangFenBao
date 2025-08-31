using Common.Services.Settings;
using KuaiLv.Models.Settings.Upload;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;

namespace KuaiLv.ViewModels.Settings;

public class UploadSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;
    private UploadConfiguration _configuration = new();

    public UploadSettingsViewModel(
        ISettingsService settingsService)
    {
        _settingsService = settingsService;

        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);

        // 加载配置
        LoadSettings();
    }

    public UploadConfiguration Configuration
    {
        get => _configuration;
        private set => SetProperty(ref _configuration, value);
    }

    public static Array Environments => Enum.GetValues(typeof(UploadEnvironment));

    internal DelegateCommand SaveConfigurationCommand { get; }

    private void ExecuteSaveConfiguration()
    {
        try
        {
            _settingsService.SaveSettings(Configuration);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存回传配置失败");
        }
    }

    private void LoadSettings()
    {
        Configuration = _settingsService.LoadSettings<UploadConfiguration>();
    }
}