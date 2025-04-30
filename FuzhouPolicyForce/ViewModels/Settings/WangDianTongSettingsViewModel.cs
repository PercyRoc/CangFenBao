using Common.Services.Settings;
using FuzhouPolicyForce.Models;

namespace FuzhouPolicyForce.ViewModels.Settings;

public class WangDianTongSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;
    private WangDianTongSettings _configuration = new();

    public WangDianTongSettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);

        // 加载配置
        LoadSettings();
    }

    public DelegateCommand SaveConfigurationCommand { get; }

    public WangDianTongSettings Configuration
    {
        get => _configuration;
        private set => SetProperty(ref _configuration, value);
    }

    private void LoadSettings()
    {
        try
        {
            Configuration = _settingsService.LoadSettings<WangDianTongSettings>();
        }
        catch (Exception)
        {
            // 处理加载失败情况，使用默认配置
            Configuration = new WangDianTongSettings();
        }
    }

    private void ExecuteSaveConfiguration()
    {
        _settingsService.SaveSettings(Configuration, true);
    }
} 