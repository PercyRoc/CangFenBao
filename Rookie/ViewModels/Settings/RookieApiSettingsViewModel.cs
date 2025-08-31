using Common.Services.Settings;
using Prism.Mvvm;
using Rookie.Models.Settings;
using Serilog;

namespace Rookie.ViewModels.Settings;

public class RookieApiSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;
    private RookieApiSettings _settings = new();

    public RookieApiSettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        LoadConfiguration();
    }

    public string BcrName
    {
        get => _settings.BcrName;
        set
        {
            if (_settings.BcrName != value)
            {
                _settings.BcrName = value;
                RaisePropertyChanged();
            }
        }
    }

    public string BcrCode
    {
        get => _settings.BcrCode;
        set
        {
            if (_settings.BcrCode != value)
            {
                _settings.BcrCode = value;
                RaisePropertyChanged();
            }
        }
    }

    public string ApiBaseUrl
    {
        get => _settings.ApiBaseUrl;
        set
        {
            if (_settings.ApiBaseUrl != value)
            {
                _settings.ApiBaseUrl = value;
                RaisePropertyChanged();
            }
        }
    }

    public void LoadConfiguration()
    {
        try
        {
            _settings = _settingsService.LoadSettings<RookieApiSettings>();
            // Manually raise property changed for all properties after loading
            RaisePropertyChanged(nameof(BcrName));
            RaisePropertyChanged(nameof(BcrCode));
            RaisePropertyChanged(nameof(ApiBaseUrl));
            Log.Debug("Rookie API settings loaded.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load Rookie API settings.");
            _settings = new RookieApiSettings(); // Use defaults if loading fails
        }
    }

    public void SaveConfiguration()
    {
        try
        {
            _settingsService.SaveSettings(_settings);
            Log.Information("Rookie API settings saved successfully.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save Rookie API settings.");
            throw;
        }
    }
}