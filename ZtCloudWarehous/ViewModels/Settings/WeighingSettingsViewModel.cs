using Common.Services.Settings;
using Common.Services.Ui;
using Serilog;

namespace ZtCloudWarehous.ViewModels.Settings;

internal class WeighingSettingsViewModel : BindableBase
{
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;

    public WeighingSettingsViewModel(
        ISettingsService settingsService,
        INotificationService notificationService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;
        
        // 加载配置
        try
        {
            Settings = _settingsService.LoadSettings<WeighingSettings>();
            Log.Information("称重设置加载成功");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载称重设置时发生错误");
            _notificationService.ShowError("加载称重设置时发生错误");
            Settings = new WeighingSettings();
        }

        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);
    }

    public WeighingSettings Settings { get; }

    internal DelegateCommand SaveConfigurationCommand { get; }

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

            _notificationService.ShowSuccess("称重设置已保存");
            Log.Information("称重设置已保存");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存称重设置时发生错误");
            _notificationService.ShowError("保存称重设置时发生错误");
        }
    }
}