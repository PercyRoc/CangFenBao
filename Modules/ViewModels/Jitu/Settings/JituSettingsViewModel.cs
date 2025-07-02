using Common.Services.Settings;
using Modules.Models.Jitu.Settings;
using Serilog;

namespace Modules.ViewModels.Jitu.Settings;

public class JituSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;
    private JituApiSettings _jituApiSettings;

    public JituSettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        LoadSettings();
        SaveCommand = new DelegateCommand(SaveSettings);
    }

    /// <summary>
    ///     极兔OpScan接口地址
    /// </summary>
    public string OpScanUrl
    {
        get => _jituApiSettings.OpScanUrl;
        set
        {
            _jituApiSettings.OpScanUrl = value;
            RaisePropertyChanged();
        }
    }

    /// <summary>
    ///     设备编号
    /// </summary>
    public string DeviceCode
    {
        get => _jituApiSettings.DeviceCode;
        set
        {
            _jituApiSettings.DeviceCode = value;
            RaisePropertyChanged();
        }
    }

    /// <summary>
    ///     设备名称
    /// </summary>
    public string DeviceName
    {
        get => _jituApiSettings.DeviceName;
        set
        {
            _jituApiSettings.DeviceName = value;
            RaisePropertyChanged();
        }
    }

    /// <summary>
    ///     条码前缀（分号分隔）
    /// </summary>
    public string BarcodePrefixes
    {
        get => _jituApiSettings.BarcodePrefixes;
        set
        {
            _jituApiSettings.BarcodePrefixes = value;
            RaisePropertyChanged();
        }
    }

    public DelegateCommand SaveCommand { get; }

    private void LoadSettings()
    {
        _jituApiSettings = _settingsService.LoadSettings<JituApiSettings>();
        Log.Information("JituApiSettings loaded for ViewModel.");
    }

    private void SaveSettings()
    {
        _settingsService.SaveSettings(_jituApiSettings);
        Log.Information("JituApiSettings saved.");
    }
}