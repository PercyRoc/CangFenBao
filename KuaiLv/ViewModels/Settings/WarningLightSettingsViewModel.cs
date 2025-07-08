using Common.Services.Settings;
using KuaiLv.Models.Settings.Warning;

namespace KuaiLv.ViewModels.Settings;

/// <summary>
///     警示灯设置视图模型
/// </summary>
public class WarningLightSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;
    private WarningLightConfiguration _configuration = new();

    public WarningLightSettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;

        // 加载配置
        LoadConfiguration();

        // 监听配置变化并自动保存
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Configuration)) SaveConfiguration();
        };
    }

    /// <summary>
    ///     配置
    /// </summary>
    public WarningLightConfiguration Configuration
    {
        get => _configuration;
        set => SetProperty(ref _configuration, value);
    }

    private void LoadConfiguration()
    {
        Configuration = _settingsService.LoadSettings<WarningLightConfiguration>();
    }

    internal void SaveConfiguration()
    {
        _settingsService.SaveSettings(Configuration);
    }
}