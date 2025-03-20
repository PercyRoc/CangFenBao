using Common.Services.Settings;
using Common.Services.Ui;
using PlateTurnoverMachine.Models.Settings;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;

namespace PlateTurnoverMachine.ViewModels.Settings;

public class ChuteSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;
    private ChuteSettings _settings;

    public ChuteSettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        SaveConfigurationCommand = new DelegateCommand(SaveConfiguration);
        LoadConfiguration();
    }

    public ChuteSettings Settings
    {
        get => _settings;
        set => SetProperty(ref _settings, value);
    }

    public DelegateCommand SaveConfigurationCommand { get; }

    private void LoadConfiguration()
    {
        try
        {
            Settings = _settingsService.LoadSettings<ChuteSettings>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载格口设置失败");
        }
    }

    public void SaveConfiguration()
    {
        try
        {
            _settingsService.SaveSettings(Settings);
            Log.Information("格口设置已保存");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存格口设置失败");
            throw;
        }
    }
} 