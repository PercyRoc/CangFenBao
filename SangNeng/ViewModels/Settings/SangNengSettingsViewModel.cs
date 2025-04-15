using Common.Services.Settings;
using Common.Services.Ui;
using Prism.Commands;
using Prism.Mvvm;
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
            var results = _settingsService.SaveSettings(Settings, true);
            if (results.Length > 0)
            {
                var errorMessage = string.Join("\n", results.Select(r => r.ErrorMessage));
                _notificationService.ShowError($"Failed to save settings：\n{errorMessage}");
                return;
            }

            _notificationService.ShowSuccess("SangNeng server settings saved successfully");
            Log.Information("SangNeng server settings saved");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error saving SangNeng server settings");
            _notificationService.ShowError("Error saving SangNeng server settings");
        }
    }
}