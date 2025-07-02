using Common.Services.Settings;
using Common.Services.Ui;
using Serilog;
using XinBeiYang.Models;

namespace XinBeiYang.ViewModels.Settings;

public class HostSettingsViewModel : BindableBase
{
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;
    private HostConfiguration _configuration = new();

    public HostSettingsViewModel(
        ISettingsService settingsService,
        INotificationService notificationService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;

        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);

        // 加载配置
        LoadConfiguration();
    }

    internal DelegateCommand SaveConfigurationCommand { get; }

    public HostConfiguration Configuration
    {
        get => _configuration;
        private set => SetProperty(ref _configuration, value);
    }

    private void LoadConfiguration()
    {
        try
        {
            Configuration = _settingsService.LoadSettings<HostConfiguration>();
            Log.Information("主机配置已加载");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载主机配置时发生错误");
            _notificationService.ShowError("加载配置失败");
        }
    }

    private void ExecuteSaveConfiguration()
    {
        try
        {
            // 验证配置的合理性
            if (!ValidateConfiguration())
            {
                return; // 验证失败，不保存
            }

            _settingsService.SaveSettings(Configuration);
            Log.Information("主机配置已保存: AckTimeout={AckTimeout}s, ResultTimeout={ResultTimeout}s",
                Configuration.UploadAckTimeoutSeconds, Configuration.UploadResultTimeoutSeconds);
            _notificationService.ShowSuccess("配置保存成功");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存主机配置时发生错误");
            _notificationService.ShowError("保存配置失败");
        }
    }

    private bool ValidateConfiguration()
    {
        // 验证初始确认超时时间
        if (Configuration.UploadAckTimeoutSeconds < 1 || Configuration.UploadAckTimeoutSeconds > 300)
        {
            _notificationService.ShowWarning("初始确认超时时间应在1-300秒之间");
            return false;
        }

        // 验证最终结果超时时间
        if (Configuration.UploadResultTimeoutSeconds < 10 || Configuration.UploadResultTimeoutSeconds > 600)
        {
            _notificationService.ShowWarning("最终结果超时时间应在10-600秒之间");
            return false;
        }

        // 验证初始确认超时时间应该小于最终结果超时时间
        if (Configuration.UploadAckTimeoutSeconds >= Configuration.UploadResultTimeoutSeconds)
        {
            _notificationService.ShowWarning("初始确认超时时间应该小于最终结果超时时间");
            return false;
        }

        // 验证倒计时时间
        if (Configuration.UploadCountdownSeconds < 0 || Configuration.UploadCountdownSeconds > 60)
        {
            _notificationService.ShowWarning("倒计时时间应在0-60秒之间");
            return false;
        }

        return true;
    }
}