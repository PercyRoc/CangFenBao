using Common.Services.Settings;
using Common.Services.Ui;
using DeviceService.DataSourceDevices.Camera.Models.Camera;
using DeviceService.DataSourceDevices.Camera.Models.Camera.Enums;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;
using Prism.Services.Dialogs;
using System.Diagnostics;
using System.Windows;
using System.Windows.Forms;

namespace SharedUI.ViewModels.Settings;

public class CameraSettingsViewModel : BindableBase
{
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;
    private readonly IDialogService _dialogService;
    private CameraSettings _configuration = new();
    private CameraManufacturer _initialManufacturer;
    private CameraType _initialCameraType;

    public CameraSettingsViewModel(
        ISettingsService settingsService,
        INotificationService notificationService,
        IDialogService dialogService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;
        _dialogService = dialogService;

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
            var manufacturerChanged = Configuration.Manufacturer != _initialManufacturer;
            var cameraTypeChanged = Configuration.CameraType != _initialCameraType;
            var restartNeeded = manufacturerChanged || cameraTypeChanged;

            var results = _settingsService.SaveSettings(Configuration, true);
            if (results.Length > 0)
            {
                return;
            }

            if (!restartNeeded) return;
            _initialManufacturer = Configuration.Manufacturer;
            _initialCameraType = Configuration.CameraType;

            _dialogService.ShowDialog("ConfirmationDialog", new DialogParameters
            {
                { "title", "需要重启" },
                { "message", "相机厂商或类型已更改，需要重启应用程序才能生效。是否立即重启？" }
            }, result =>
            {
                if (result.Result != ButtonResult.OK) return;
                try
                {
                    var currentProcess = Process.GetCurrentProcess();
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = currentProcess.MainModule?.FileName,
                        UseShellExecute = true
                    };

                    Process.Start(startInfo);

                    System.Windows.Application.Current.Shutdown();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "应用程序重启失败");
                    _notificationService.ShowErrorWithToken("应用程序重启失败，请手动重启。", "SettingWindowGrowl");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存相机配置失败");
            _notificationService.ShowErrorWithToken("保存相机配置失败", "SettingWindowGrowl");
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
            if (result == System.Windows.Forms.DialogResult.OK)
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
        _initialManufacturer = Configuration.Manufacturer;
        _initialCameraType = Configuration.CameraType;
    }
}