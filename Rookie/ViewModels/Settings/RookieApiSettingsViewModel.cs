using Common.Services.Settings;
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
            if (_settings.BcrName == value) return;
            _settings.BcrName = value;
            RaisePropertyChanged();
        }
    }

    public string BcrCode
    {
        get => _settings.BcrCode;
        set
        {
            if (_settings.BcrCode == value) return;
            _settings.BcrCode = value;
            RaisePropertyChanged();
        }
    }

    public string ApiBaseUrl
    {
        get => _settings.ApiBaseUrl;
        set
        {
            if (_settings.ApiBaseUrl == value) return;
            _settings.ApiBaseUrl = value;
            RaisePropertyChanged();
        }
    }

    public string Source
    {
        get => _settings.Source;
        set
        {
            if (_settings.Source == value) return;
            _settings.Source = value;
            RaisePropertyChanged();
        }
    }

    public string ImageUploadUrl
    {
        get => _settings.ImageUploadUrl;
        set
        {
            if (_settings.ImageUploadUrl == value) return;
            _settings.ImageUploadUrl = value;
            RaisePropertyChanged();
        }
    }

    private void LoadConfiguration()
    {
        try
        {
            _settings = _settingsService.LoadSettings<RookieApiSettings>();
            RaisePropertyChanged(nameof(BcrName));
            RaisePropertyChanged(nameof(BcrCode));
            RaisePropertyChanged(nameof(ApiBaseUrl));
            RaisePropertyChanged(nameof(Source));
            Log.Debug("Rookie API settings loaded.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load Rookie API settings.");
            _settings = new RookieApiSettings();
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