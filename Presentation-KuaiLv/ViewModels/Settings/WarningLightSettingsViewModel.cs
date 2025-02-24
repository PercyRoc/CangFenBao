using CommonLibrary.Services;
using Presentation_CommonLibrary.Services;
using Presentation_KuaiLv.Models.Settings.Warning;
using Presentation_KuaiLv.Services.Warning;
using Prism.Mvvm;

namespace Presentation_KuaiLv.ViewModels.Settings;

/// <summary>
/// 警示灯设置视图模型
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
            if (e.PropertyName == nameof(Configuration))
            {
                SaveConfiguration();
            }
        };
    }

    /// <summary>
    /// 配置
    /// </summary>
    public WarningLightConfiguration Configuration
    {
        get => _configuration;
        set => SetProperty(ref _configuration, value);
    }

    private void LoadConfiguration()
    {
        Configuration = _settingsService.LoadConfiguration<WarningLightConfiguration>();
    }

    public void SaveConfiguration()
    {
        _settingsService.SaveConfiguration(Configuration);
    }
} 