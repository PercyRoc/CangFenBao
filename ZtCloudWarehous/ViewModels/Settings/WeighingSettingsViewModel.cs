using Common.Services.Settings;
using Common.Services.Ui;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;

namespace Presentation_ZtCloudWarehous.ViewModels.Settings;

public class WeighingSettingsViewModel : BindableBase
{
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;

    public WeighingSettingsViewModel(
        ISettingsService settingsService,
        INotificationService notificationService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;
        Settings = _settingsService.LoadSettings<WeighingSettings>();

        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);
    }

    public WeighingSettings Settings { get; }

    public DelegateCommand SaveConfigurationCommand { get; }

    private void ExecuteSaveConfiguration()
    {
        try
        {
            var results = _settingsService.SaveSettings(Settings, true);
            if (results.Length > 0)
            {
                var errorMessage = string.Join("\n", results.Select(r => r.ErrorMessage));
                _notificationService.ShowError($"保存设置失败：\n{errorMessage}");
                return;
            }

            Log.Information("称重设置已保存");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存称重设置时发生错误");
            _notificationService.ShowError("保存称重设置时发生错误");
        }
    }
}