using System.IO;
using System.Windows.Forms;
using Common.Services.Settings;
using Common.Services.Ui;
using DeviceService.DataSourceDevices.Camera.Models.Camera;
using Serilog;
using DialogResult = System.Windows.Forms.DialogResult;

namespace Sunnen.ViewModels.Settings;

public class VolumeSettingsViewModel : BindableBase
{
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;
    private VolumeSettings? _configuration;

    public VolumeSettingsViewModel(
        ISettingsService settingsService,
        INotificationService notificationService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;

        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);
        BrowseFolderCommand = new DelegateCommand(ExecuteBrowseFolder);

        // Load configuration
        LoadSettings();
    }

    private VolumeSettings? Configuration
    {
        get => _configuration;
        set => SetProperty(ref _configuration, value);
    }

    public int TimeoutMs
    {
        get => Configuration?.TimeoutMs ?? 5000;
        set
        {
            if (Configuration == null) return;
            Configuration.TimeoutMs = value;
            RaisePropertyChanged();
        }
    }

    public DelegateCommand SaveConfigurationCommand { get; }
    public DelegateCommand BrowseFolderCommand { get; }

    // Add property for ImageSavePath
    public string ImageSavePath
    {
        get => Configuration?.ImageSavePath ?? "D:\\SavedImages"; // Provide default
        set
        {
            if (Configuration == null) return;
            Configuration.ImageSavePath = value;
            RaisePropertyChanged();
        }
    }

    // Add property for ImageSaveMode
    public DimensionImageSaveMode ImageSaveMode
    {
        get => Configuration?.ImageSaveMode ?? DimensionImageSaveMode.Vertical;
        set
        {
            if (Configuration == null) return;
            if (Configuration.ImageSaveMode != value)
            {
                Configuration.ImageSaveMode = value;
                RaisePropertyChanged();
            }
        }
    }

    private void ExecuteSaveConfiguration()
    {
        if (Configuration == null) return;

        // Ensure the directory exists before saving
        try
        {
            if (!string.IsNullOrWhiteSpace(Configuration.ImageSavePath) && !Directory.Exists(Configuration.ImageSavePath))
            {
                Directory.CreateDirectory(Configuration.ImageSavePath);
                Log.Information("图像保存目录不存在，已创建: {Path}", Configuration.ImageSavePath);
            }
            _settingsService.SaveSettings(Configuration);
            _notificationService.ShowSuccessWithToken("Volume camera settings saved", "SettingWindowGrowl");
        }
        catch (IOException ioEx)
        {
            Log.Error(ioEx, "创建图像保存目录或保存配置时发生IO错误: {Path}", Configuration.ImageSavePath);
            _notificationService.ShowErrorWithToken($"Error saving settings: Invalid path '{Configuration.ImageSavePath}'. {ioEx.Message}", "SettingWindowGrowl");
        }
        catch (UnauthorizedAccessException uaEx)
        {
            Log.Error(uaEx, "创建图像保存目录或保存配置时权限不足: {Path}", Configuration.ImageSavePath);
            _notificationService.ShowErrorWithToken($"Error saving settings: Permission denied for path '{Configuration.ImageSavePath}'. {uaEx.Message}", "SettingWindowGrowl");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存体积相机配置失败");
            _notificationService.ShowErrorWithToken($"Failed to save volume camera settings: {ex.Message}", "SettingWindowGrowl");
        }
    }

    private void ExecuteBrowseFolder()
    {
        try
        {
            var dialog = new FolderBrowserDialog
            {
                Description = @"Select Image Save Folder",
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(ImageSavePath) ? ImageSavePath : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };

            var result = dialog.ShowDialog();

            // Process dialog results
            if (result != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;
            ImageSavePath = dialog.SelectedPath;
            Log.Information("用户选择了新的图像保存路径: {Path}", ImageSavePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "打开文件夹选择对话框时出错");
            _notificationService.ShowErrorWithToken("Failed to open folder browser.", "SettingWindowGrowl");
        }
    }

    private void LoadSettings()
    {
        Configuration = _settingsService.LoadSettings<VolumeSettings>();
        // Ensure RaisePropertyChanged is called after loading to update all bindings
        RaisePropertyChanged(string.Empty); // Passing empty string updates all properties
    }
}