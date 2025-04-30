using Common.Services.Settings;
using Common.Services.Ui;
using Serilog;
using SortingServices.Servers.Models;

namespace SharedUI.ViewModels;

public class JushuitanSettingsViewModel : BindableBase
{
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;

    public JushuitanSettingsViewModel(
        ISettingsService settingsService,
        INotificationService notificationService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;
        Settings = _settingsService.LoadSettings<JushuitanSettings>();

        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);
    }

    public JushuitanSettings Settings { get; }

    public DelegateCommand SaveConfigurationCommand { get; }

    private void ExecuteSaveConfiguration()
    {
        try
        {
            var results = _settingsService.SaveSettings(Settings, true);
            if (results.Length > 0)
            {
                var errorMessage = string.Join("\n", results.Select(static r => r.ErrorMessage));
                _notificationService.ShowError($"保存设置失败：\n{errorMessage}");
                return;
            }

            _notificationService.ShowSuccess("聚水潭设置已保存");
            Log.Information("聚水潭设置已保存");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存聚水潭设置时发生错误");
            _notificationService.ShowError("保存聚水潭设置时发生错误");
        }
    }
}