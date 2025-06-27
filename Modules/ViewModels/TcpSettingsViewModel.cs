using Common.Services.Settings;
using Common.Services.Ui;
using Serilog;
using ShanghaiModuleBelt.Models;

namespace ShanghaiModuleBelt.ViewModels;

/// <summary>
///     TCP设置视图模型
/// </summary>
public class TcpSettingsViewModel : BindableBase
{
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;
    private TcpSettings _config;

    /// <summary>
    ///     初始化TCP设置视图模型
    /// </summary>
    /// <param name="settingsService">设置服务</param>
    /// <param name="notificationService">通知服务</param>
    public TcpSettingsViewModel(ISettingsService settingsService, INotificationService notificationService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;

        // 加载设置
        _config = _settingsService.LoadSettings<TcpSettings>();

        // 初始化命令
        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveCommand);
    }

    /// <summary>
    ///     TCP设置
    /// </summary>
    public TcpSettings Config
    {
        get => _config;
        set => SetProperty(ref _config, value);
    }

    /// <summary>
    ///     保存配置命令
    /// </summary>
    public DelegateCommand SaveConfigurationCommand { get; }

    /// <summary>
    ///     执行保存命令
    /// </summary>
    private void ExecuteSaveCommand()
    {
        try
        {
            // 保存设置
            var validationResults = _settingsService.SaveSettings(_config, true);

            if (validationResults.Length > 0)
            {
                var errorMessage = string.Join("\n", validationResults.Select(static r => r.ErrorMessage));
                _notificationService.ShowError($"保存设置失败：\n{errorMessage}");
                Log.Error("保存TCP设置失败: {ErrorMessage}", errorMessage);
                return;
            }

            Log.Information("TCP设置已保存: {Address}:{Port}", _config.Address, _config.Port);
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"保存设置时发生错误: {ex.Message}");
            Log.Error(ex, "保存TCP设置时发生错误");
        }
    }
}