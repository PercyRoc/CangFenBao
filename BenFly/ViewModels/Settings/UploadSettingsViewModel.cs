using Common.Services.Settings;
using Presentation_BenFly.Models.Upload;
using Prism.Commands;
using Prism.Mvvm;

namespace Presentation_BenFly.ViewModels.Settings;

public class UploadSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;

    private UploadConfiguration _configuration = new();

    public UploadSettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);

        // 加载配置
        LoadSettings();
    }

    public UploadConfiguration Configuration
    {
        get => _configuration;
        set => SetProperty(ref _configuration, value);
    }

    public IEnumerable<BenNiaoEnvironment> BenNiaoEnvironments => Enum.GetValues<BenNiaoEnvironment>();

    public DelegateCommand SaveConfigurationCommand { get; }

    private void ExecuteSaveConfiguration()
    {
        _settingsService.SaveSettings(Configuration);
    }

    private void LoadSettings()
    {
        Configuration = _settingsService.LoadSettings<UploadConfiguration>();
    }
}