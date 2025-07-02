using Common.Services.Settings;
using HandyControl.Controls;
using Serilog;
using ShanghaiModuleBelt.Models.Zto.Settings;

namespace ShanghaiModuleBelt.ViewModels.Zto.Settings;

/// <summary>
///     中通API设置视图模型
/// </summary>
public class ZtoApiSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;
    private ZtoApiSettings _settings;

    public ZtoApiSettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        _settings = _settingsService.LoadSettings<ZtoApiSettings>();
        SaveCommand = new DelegateCommand(ExecuteSaveSettings);
    }

    /// <summary>
    ///     中通API配置
    /// </summary>
    public ZtoApiSettings Settings
    {
        get => _settings;
        set => SetProperty(ref _settings, value);
    }

    /// <summary>
    ///     保存设置命令
    /// </summary>
    public DelegateCommand SaveCommand { get; }

    /// <summary>
    ///     执行保存设置操作
    /// </summary>
    private void ExecuteSaveSettings()
    {
        try
        {
            _settingsService.SaveSettings(Settings);
            Growl.Success("中通API设置保存成功！");
            Log.Information("中通API设置保存成功");
        }
        catch (Exception ex)
        {
            Growl.Error($"中通API设置保存失败: {ex.Message}");
            Log.Error(ex, "中通API设置保存失败");
        }
    }
}