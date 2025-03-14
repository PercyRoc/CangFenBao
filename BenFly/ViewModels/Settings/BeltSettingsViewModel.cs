using System.Collections.ObjectModel;
using System.IO.Ports;
using Common.Services.Settings;
using Common.Services.Ui;
using BenFly.Models.Settings;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;

namespace BenFly.ViewModels.Settings;

internal class BeltSettingsViewModel : BindableBase
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
        var ports = SerialPort.GetPortNames();
        Log.Debug("找到串口数量: {Count}, 串口列表: {@Ports}", ports.Length, ports);
        AvailablePortNames = new ObservableCollection<string>(ports);

        // 初始化波特率列表
        AvailableBaudRates = [9600, 19200, 38400, 57600, 115200];
        Log.Debug("波特率列表初始化完成: {@BaudRates}", AvailableBaudRates);

        // 初始化数据位列表
        AvailableDataBits = [5, 6, 7, 8];
        Log.Debug("数据位列表初始化完成: {@DataBits}", AvailableDataBits);

        // 初始化校验位列表
        AvailableParities = new ObservableCollection<Parity>(Enum.GetValues<Parity>());
        Log.Debug("校验位列表初始化完成: {@Parities}", AvailableParities);

        // 初始化停止位列表
        AvailableStopBits = new ObservableCollection<StopBits>(Enum.GetValues<StopBits>());
        Log.Debug("停止位列表初始化完成: {@StopBits}", AvailableStopBits);

        // 加载配置
        Settings = _settingsService.LoadSettings<BeltSettings>();
        Log.Debug("加载的设置: {@Settings}", Settings);

        // 如果串口为空，设置默认值
        if (string.IsNullOrEmpty(Settings.PortName))
        {
            Settings.PortName = AvailablePortNames.FirstOrDefault() ?? string.Empty;
            Log.Debug("设置默认串口: {PortName}", Settings.PortName);
        }

        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);
    }

    public BeltSettings Settings { get; }

    public ObservableCollection<string> AvailablePortNames { get; }
    public ObservableCollection<int> AvailableBaudRates { get; }
    public ObservableCollection<int> AvailableDataBits { get; }
    public ObservableCollection<Parity> AvailableParities { get; }
    public ObservableCollection<StopBits> AvailableStopBits { get; }

    internal DelegateCommand SaveConfigurationCommand { get; }

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