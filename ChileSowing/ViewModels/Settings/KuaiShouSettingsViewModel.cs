using System.Windows.Input;
using ChileSowing.Models.Settings;
using ChileSowing.Services;
using Common.Services.Settings;
using Common.Services.Ui;
using Serilog;

namespace ChileSowing.ViewModels.Settings;

public class KuaiShouSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;
    private readonly IKuaiShouApiService _kuaiShouApiService;
    private readonly INotificationService _notificationService;
    private KuaiShouSettings _settings;
    private bool _isTesting;

    public KuaiShouSettingsViewModel(
        ISettingsService settingsService, 
        IKuaiShouApiService kuaiShouApiService,
        INotificationService notificationService)
    {
        _settingsService = settingsService;
        _kuaiShouApiService = kuaiShouApiService;
        _notificationService = notificationService;
        
        _settings = _settingsService.LoadSettings<KuaiShouSettings>();
        
        TestConnectionCommand = new DelegateCommand(async () => await ExecuteTestConnection(), () => !_isTesting);
        ResetToDefaultCommand = new DelegateCommand(ExecuteResetToDefault);
    }

    public KuaiShouSettings Settings
    {
        get => _settings;
        set => SetProperty(ref _settings, value);
    }

    public bool IsTesting
    {
        get => _isTesting;
        set
        {
            SetProperty(ref _isTesting, value);
            TestConnectionCommand.RaiseCanExecuteChanged();
        }
    }

    public DelegateCommand TestConnectionCommand { get; }
    public ICommand ResetToDefaultCommand { get; }

    private async Task ExecuteTestConnection()
    {
        if (IsTesting) return;

        IsTesting = true;
        try
        {
            SaveSettings();
            
            _notificationService.ShowWarning("Testing KuaiShou API connection...");
            var isConnected = await _kuaiShouApiService.TestConnectionAsync();
            
            if (isConnected)
            {
                _notificationService.ShowSuccess("KuaiShou API connection test successful");
                Log.Information("KuaiShou API connection test successful");
            }
            else
            {
                _notificationService.ShowError("KuaiShou API connection test failed");
                Log.Warning("KuaiShou API connection test failed");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during KuaiShou API connection test");
            _notificationService.ShowError($"Connection test error: {ex.Message}");
        }
        finally
        {
            IsTesting = false;
        }
    }

    private void ExecuteResetToDefault()
    {
        Settings = new KuaiShouSettings();
        _notificationService.ShowWarning("Settings reset to default values");
    }

    public void SaveSettings()
    {
        try
        {
            _settingsService.SaveSettings(Settings);
            Log.Information("KuaiShou settings saved successfully");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save KuaiShou settings");
            throw;
        }
    }
} 