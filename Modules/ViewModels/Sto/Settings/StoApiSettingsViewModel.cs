using Common.Services.Settings;
using HandyControl.Controls;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;
using ShanghaiModuleBelt.Models.Sto.Settings;

namespace ShanghaiModuleBelt.ViewModels.Sto.Settings;

/// <summary>
///     申通快递API设置视图模型
/// </summary>
public class StoApiSettingsViewModel : BindableBase
{
    // private readonly IDialogService _dialogService;
    private readonly ISettingsService _settingsService;
    private StoApiSettings _settings;

    public StoApiSettingsViewModel(ISettingsService settingsService
        // IDialogService dialogService
    )
    {
        _settingsService = settingsService;
        // _dialogService = dialogService;
        _settings = _settingsService.LoadSettings<StoApiSettings>();

        SaveCommand = new DelegateCommand(Save);
    }

    public StoApiSettings Settings
    {
        get => _settings;
        set => SetProperty(ref _settings, value);
    }

    public DelegateCommand SaveCommand { get; }

    private void Save()
    {
        _settingsService.SaveSettings(Settings);
        Growl.Success("申通API设置已保存！");
        Log.Information("申通API设置已保存。");
    }
}