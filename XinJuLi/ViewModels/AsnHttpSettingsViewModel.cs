using Common.Services.Settings;
using Serilog;
using XinJuLi.Models.ASN;

namespace XinJuLi.ViewModels;

/// <summary>
///     ASN HTTP服务设置视图模型
/// </summary>
public class AsnHttpSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;

    /// <summary>
    ///     构造函数
    /// </summary>
    /// <param name="settingsService">设置服务</param>
    public AsnHttpSettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);
        Settings = _settingsService.LoadSettings<AsnSettings>();
    }
    public AsnSettings Settings { get; }

    /// <summary>
    ///     保存配置命令
    /// </summary>
    public DelegateCommand SaveConfigurationCommand { get; }

    /// <summary>
    ///     执行保存配置
    /// </summary>
    private void ExecuteSaveConfiguration()
    {
        try
        {
            Log.Information("保存ASN HTTP服务配置");
            _settingsService.SaveSettings(Settings);
            Log.Information("ASN HTTP服务配置已保存");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存ASN HTTP服务配置时发生错误");
        }
    }
}