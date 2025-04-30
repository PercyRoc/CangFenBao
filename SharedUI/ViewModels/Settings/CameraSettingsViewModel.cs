using Common.Services.Settings;
using Common.Services.Ui;
using DeviceService.DataSourceDevices.Camera.Models.Camera;
using DeviceService.DataSourceDevices.Camera.Models.Camera.Enums;
using Serilog;
using System.Windows.Forms;
using DialogResult = System.Windows.Forms.DialogResult;

namespace SharedUI.ViewModels.Settings;

public class CameraSettingsViewModel : BindableBase
{
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;
    private CameraSettings _configuration = new();

    public CameraSettingsViewModel(
        ISettingsService settingsService,
        INotificationService notificationService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;

        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);
        BrowseImagePathCommand = new DelegateCommand(BrowseImagePath);

        // 加载配置
        LoadSettings();
    }

    public CameraSettings Configuration
    {
        get => _configuration;
        private set => SetProperty(ref _configuration, value);
    }

    public static Array Manufacturers => Enum.GetValues(typeof(CameraManufacturer));
    public static Array CameraTypes => Enum.GetValues(typeof(CameraType));
    public static Array ImageFormats => Enum.GetValues(typeof(ImageFormat));

    public DelegateCommand SaveConfigurationCommand { get; }
    public DelegateCommand BrowseImagePathCommand { get; }

    private void ExecuteSaveConfiguration()
    {
        try
        {
            _settingsService.SaveSettings(Configuration, true);
        }
        catch (Exception ex)
        {
            _notificationService.ShowErrorWithToken($"保存相机配置失败{ex.Message}", "SettingWindowGrowl");
        }
    }

    private void BrowseImagePath()
    {
        try
        {
            var dialog = new FolderBrowserDialog
            {
                Description = "选择图像保存路径",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true
            };

            if (!string.IsNullOrEmpty(Configuration.ImageSavePath) && System.IO.Directory.Exists(Configuration.ImageSavePath))
            {
                dialog.SelectedPath = Configuration.ImageSavePath;
            }

            var result = dialog.ShowDialog();
            if (result == DialogResult.OK)
            {
                Configuration.ImageSavePath = dialog.SelectedPath;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "选择图像保存路径时发生错误");
            _notificationService.ShowError("选择图像保存路径失败：" + ex.Message);
        }
    }

    private void LoadSettings()
    {
        Configuration = _settingsService.LoadSettings<CameraSettings>();
    }
}