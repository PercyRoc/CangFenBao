using Common.Services.Settings;
using Common.Services.Ui;
using Serilog;
using Server.JuShuiTan.Models;

namespace Server.JuShuiTan.ViewModels;

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
            _settingsService.SaveSettings(Settings, true);
            Log.Information("聚水潭设置已保存");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存聚水潭设置时发生错误");
            _notificationService.ShowError("保存聚水潭设置时发生错误");
        }
    }
}