using BenFly.Models.Upload;
using Common.Services.Settings;

namespace BenFly.ViewModels.Settings;

internal class UploadSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;

    private UploadConfiguration _configuration = new();

    public UploadSettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);
        BenNiaoEnvironments = GetBenNiaoEnvironments();

        // 加载配置
        LoadSettings();
    }

    public UploadConfiguration Configuration
    {
        get => _configuration;
        private set => SetProperty(ref _configuration, value);
    }

    public IEnumerable<BenNiaoEnvironment> BenNiaoEnvironments { get; }

    internal DelegateCommand SaveConfigurationCommand { get; }

    private static IEnumerable<BenNiaoEnvironment> GetBenNiaoEnvironments()
    {
        return Enum.GetValues<BenNiaoEnvironment>();
    }

    private void ExecuteSaveConfiguration()
    {
        _settingsService.SaveSettings(Configuration);
    }

    private void LoadSettings()
    {
        Configuration = _settingsService.LoadSettings<UploadConfiguration>();
    }
}