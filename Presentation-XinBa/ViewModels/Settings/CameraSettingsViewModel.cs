using System.Windows.Input;
using CommonLibrary.Services;
using Prism.Commands;
using Prism.Mvvm;
using Presentation_XinBa.Services.Models;

namespace Presentation_XinBa.ViewModels.Settings;

public class CameraSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;
    private CameraConnectionSettings? _settings;
    private string? _serverIp;
    private int _serverPort;
    private int _reconnectIntervalMs;
    private int _connectionTimeoutMs;
    private string? _imageSavePath;
    private string? _statusMessage;
    private bool _isSaving;

    public CameraSettingsViewModel(ISettingsService settingsService)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        _settingsService = settingsService;

        SaveConfigurationCommand = new DelegateCommand(SaveSettings, CanSaveSettings)
            .ObservesProperty(() => IsSaving);
        BrowseImagePathCommand = new DelegateCommand(BrowseImagePath);

        LoadSettings();
    }

    public string? ServerIp
    {
        get => _serverIp;
        set => SetProperty(ref _serverIp, value);
    }

    public int ServerPort
    {
        get => _serverPort;
        set => SetProperty(ref _serverPort, value);
    }

    public int ReconnectIntervalMs
    {
        get => _reconnectIntervalMs;
        set => SetProperty(ref _reconnectIntervalMs, value);
    }

    public int ConnectionTimeoutMs
    {
        get => _connectionTimeoutMs;
        set => SetProperty(ref _connectionTimeoutMs, value);
    }

    public string? ImageSavePath
    {
        get => _imageSavePath;
        set => SetProperty(ref _imageSavePath, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    private bool IsSaving
    {
        get => _isSaving;
        set => SetProperty(ref _isSaving, value);
    }

    public ICommand SaveConfigurationCommand { get; }
    public ICommand BrowseImagePathCommand { get; }

    private void LoadSettings()
    {
        try
        {
            _settings = _settingsService.LoadConfiguration<CameraConnectionSettings>();

            ServerIp = _settings.ServerIp;
            ServerPort = _settings.ServerPort;
            ReconnectIntervalMs = _settings.ReconnectIntervalMs;
            ConnectionTimeoutMs = _settings.ConnectionTimeoutMs;
            ImageSavePath = _settings.ImageSavePath;

            StatusMessage = "Settings loaded";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load settings: {ex.Message}";
        }
    }

    private void SaveSettings()
    {
        try
        {
            IsSaving = true;

            if (_settings != null)
            {
                _settings.ServerIp = ServerIp;
                _settings.ServerPort = ServerPort;
                _settings.ReconnectIntervalMs = ReconnectIntervalMs;
                _settings.ConnectionTimeoutMs = ConnectionTimeoutMs;
                _settings.ImageSavePath = ImageSavePath;

                _settingsService.SaveConfiguration(_settings);
            }

            StatusMessage = "Settings saved";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to save settings: {ex.Message}";
        }
        finally
        {
            IsSaving = false;
        }
    }

    private bool CanSaveSettings()
    {
        return !IsSaving;
    }

    private void BrowseImagePath()
    {
        try
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select image save path",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                ImageSavePath = dialog.SelectedPath;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to select path: {ex.Message}";
        }
    }
}