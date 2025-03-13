using Common.Services.Settings;
using Common.Services.Ui;
using Presentation_SeedingWall.Models;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;

namespace Presentation_SeedingWall.ViewModels.Settings;

/// <summary>
///     聚水潭设置页面ViewModel
/// </summary>
public class JuShuiTanSettingsViewModel : BindableBase
{
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;
    private JuShuiTanSettings _configuration = new();

    /// <summary>
    ///     构造函数
    /// </summary>
    /// <param name="settingsService">设置服务</param>
    /// <param name="notificationService">通知服务</param>
    public JuShuiTanSettingsViewModel(
        ISettingsService settingsService,
        INotificationService notificationService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;

        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);

        // 加载配置
        LoadSettings();
    }

    /// <summary>
    ///     配置
    /// </summary>
    public JuShuiTanSettings Configuration
    {
        get => _configuration;
        private set => SetProperty(ref _configuration, value);
    }

    /// <summary>
    ///     保存配置命令
    /// </summary>
    public DelegateCommand SaveConfigurationCommand { get; }

    /// <summary>
    ///     保存配置
    /// </summary>
    private void ExecuteSaveConfiguration()
    {
        try
        {
            _settingsService.SaveSettings(Configuration);
            Log.Information("聚水潭设置已保存");
            _notificationService.ShowSuccessWithToken("聚水潭设置已保存", "SettingWindowGrowl");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存聚水潭设置失败");
            _notificationService.ShowErrorWithToken($"保存聚水潭设置失败: {ex.Message}", "SettingWindowGrowl");
        }
    }

    /// <summary>
    ///     加载设置
    /// </summary>
    private void LoadSettings()
    {
        try
        {
            var settings = _settingsService.LoadSettings<JuShuiTanSettings>();
            Configuration = settings;
            Log.Information("聚水潭设置已加载");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载聚水潭设置失败");
            _notificationService.ShowErrorWithToken($"加载聚水潭设置失败: {ex.Message}", "SettingWindowGrowl");
        }
    }
}