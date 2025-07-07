using Common.Services.Notifications;
using Common.Services.Settings;
using Serilog;
using Sunnen.Models.Settings;

namespace Sunnen.ViewModels.Settings;

public class SangNengSettingsViewModel : BindableBase
{
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;

    public SangNengSettingsViewModel(
        ISettingsService settingsService,
        INotificationService notificationService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;
        Settings = _settingsService.LoadSettings<SangNengSettings>();

        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);
    }

    public SangNengSettings Settings { get; }

    public DelegateCommand SaveConfigurationCommand { get; }

    private void ExecuteSaveConfiguration()
    {
        try
        {
            Log.Information("正在保存桑能设置: Username={Username}, Password={Password}, Sign={Sign}", 
                Settings.Username, 
                Settings.Password, 
                Settings.Sign);

            var results = _settingsService.SaveSettings(Settings, true);
            if (results.Length > 0)
            {
                var errorMessage = string.Join("\n", results.Select(r => r.ErrorMessage));
                Log.Error("保存桑能设置失败: {ErrorMessage}", errorMessage);
                _notificationService.ShowError($"保存设置失败：\n{errorMessage}");
                return;
            }

            _notificationService.ShowSuccess("桑能服务器设置已保存");
            Log.Information("桑能服务器设置已保存");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存桑能服务器设置时发生错误");
            _notificationService.ShowError("保存桑能服务器设置时发生错误");
        }
    }
}