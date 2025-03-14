using System.Collections.ObjectModel;
using System.IO.Ports;
using Common.Services.Settings;
using Common.Services.Ui;
using Presentation_BenFly.Models.Settings;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;

namespace Presentation_BenFly.ViewModels.Settings;

public class BeltSettingsViewModel : BindableBase
{
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;

    public BeltSettingsViewModel(
        INotificationService notificationService,
        ISettingsService settingsService)
    {
        _notificationService = notificationService;
        _settingsService = settingsService;

        // 初始化可用串口列表
        AvailablePortNames = new ObservableCollection<string>(SerialPort.GetPortNames());

        // 初始化波特率列表
        AvailableBaudRates = [9600, 19200, 38400, 57600, 115200];

        // 初始化数据位列表
        AvailableDataBits = [5, 6, 7, 8];

        // 初始化校验位列表
        AvailableParities = new ObservableCollection<Parity>(Enum.GetValues<Parity>());

        // 初始化停止位列表
        AvailableStopBits = new ObservableCollection<StopBits>(Enum.GetValues<StopBits>());

        // 加载配置
        Settings = _settingsService.LoadSettings<BeltSettings>();

        // 如果串口为空，设置默认值
        if (string.IsNullOrEmpty(Settings.PortName))
            Settings.PortName = AvailablePortNames.FirstOrDefault() ?? string.Empty;

        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);
    }

    public BeltSettings Settings { get; }

    public ObservableCollection<string> AvailablePortNames { get; }
    public ObservableCollection<int> AvailableBaudRates { get; }
    public ObservableCollection<int> AvailableDataBits { get; }
    public ObservableCollection<Parity> AvailableParities { get; }
    public ObservableCollection<StopBits> AvailableStopBits { get; }

    public DelegateCommand SaveConfigurationCommand { get; }

    private void ExecuteSaveConfiguration()
    {
        try
        {
            _settingsService.SaveSettings(Settings);
            Log.Information("皮带串口设置已保存");
            _notificationService.ShowSuccess("皮带串口设置已保存");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存皮带串口设置时发生错误");
            _notificationService.ShowError("保存皮带串口设置时发生错误");
        }
    }
}