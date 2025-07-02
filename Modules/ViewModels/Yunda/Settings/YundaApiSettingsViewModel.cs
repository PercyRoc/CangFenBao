using Common.Services.Settings;
using HandyControl.Controls;
using Serilog;
using ShanghaiModuleBelt.Models.Yunda.Settings;

namespace ShanghaiModuleBelt.ViewModels.Yunda.Settings;

/// <summary>
///     韵达API设置视图模型
/// </summary>
public class YundaApiSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;
    private YundaApiSettings _settings;

    public YundaApiSettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        _settings = _settingsService.LoadSettings<YundaApiSettings>();
        SaveSettingsCommand = new DelegateCommand(ExecuteSaveSettings);
    }

    /// <summary>
    ///     韵达API配置
    /// </summary>
    public YundaApiSettings Settings
    {
        get => _settings;
        set => SetProperty(ref _settings, value);
    }

    /// <summary>
    ///     保存设置命令
    /// </summary>
    public DelegateCommand SaveSettingsCommand { get; }

    /// <summary>
    ///     执行保存设置操作
    /// </summary>
    private void ExecuteSaveSettings()
    {
        try
        {
            _settingsService.SaveSettings(Settings);
            Growl.Success("韵达API设置保存成功！");
            Log.Information("韵达API设置保存成功");
        }
        catch (Exception ex)
        {
            Growl.Error($"韵达API设置保存失败: {ex.Message}");
            Log.Error(ex, "韵达API设置保存失败");
        }
    }
}