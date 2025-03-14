using Common.Services.Settings;
using Common.Services.Ui;
using Presentation_SeedingWall.Models;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;

namespace Presentation_SeedingWall.ViewModels.Settings;

/// <summary>
///     PLC设置视图模型
/// </summary>
public class PlcSettingsViewModel : BindableBase
{
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;
    private PlcSettings _configuration;

    /// <summary>
    ///     构造函数
    /// </summary>
    /// <param name="settingsService">设置服务</param>
    /// <param name="notificationService">通知服务</param>
    public PlcSettingsViewModel(ISettingsService settingsService, INotificationService notificationService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;
        _configuration = new PlcSettings();

        SaveConfigurationCommand = new DelegateCommand(SaveConfiguration);

        LoadConfiguration();
    }

    /// <summary>
    ///     配置
    /// </summary>
    public PlcSettings Configuration
    {
        get => _configuration;
        set => SetProperty(ref _configuration, value);
    }

    /// <summary>
    ///     保存配置命令
    /// </summary>
    public DelegateCommand SaveConfigurationCommand { get; }

    /// <summary>
    ///     加载配置
    /// </summary>
    private void LoadConfiguration()
    {
        try
        {
            var config = _settingsService.LoadSettings<PlcSettings>();
            Configuration = config;
            Log.Information("已加载PLC配置");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载PLC配置时发生错误");
            _notificationService.ShowError("加载PLC配置失败");
        }
    }

    /// <summary>
    ///     保存配置
    /// </summary>
    private void SaveConfiguration()
    {
        try
        {
            _settingsService.SaveSettings(Configuration);
            Log.Information("已保存PLC配置");
            _notificationService.ShowSuccess("保存成功");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存PLC配置时发生错误");
            _notificationService.ShowError("保存PLC配置失败");
        }
    }
}