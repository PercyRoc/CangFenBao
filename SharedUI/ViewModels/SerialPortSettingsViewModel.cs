using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Windows.Input;
using Common.Services.Settings;
using Common.Services.Ui;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;
using SortingServices.Car;

namespace SharedUI.ViewModels;

/// <summary>
///     串口通讯设置视图模型
/// </summary>
public class SerialPortSettingsViewModel : BindableBase
{
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;
    private bool _isPortOpen;
    private SerialPortSettings _settings;

    public SerialPortSettingsViewModel(ISettingsService settingsService, INotificationService notificationService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;
        _settings = new SerialPortSettings();

        SaveConfigurationCommand = new DelegateCommand(SaveConfiguration);
        RefreshPortsCommand = new DelegateCommand(RefreshPorts);

        LoadConfiguration();
        RefreshPorts();
    }

    /// <summary>
    ///     串口设置
    /// </summary>
    public SerialPortSettings Settings
    {
        get => _settings;
        private set => SetProperty(ref _settings, value);
    }

    /// <summary>
    ///     可用串口列表
    /// </summary>
    public ObservableCollection<string> AvailablePorts { get; } = [];

    /// <summary>
    ///     可用波特率列表
    /// </summary>
    public ObservableCollection<int> AvailableBaudRates { get; } =
        [1200, 2400, 4800, 9600, 14400, 19200, 38400, 57600, 115200, 128000, 256000];

    /// <summary>
    ///     可用数据位列表
    /// </summary>
    public ObservableCollection<int> AvailableDataBits { get; } = [5, 6, 7, 8];

    /// <summary>
    ///     可用停止位列表
    /// </summary>
    public ObservableCollection<StopBits> AvailableStopBits { get; } =
        [StopBits.None, StopBits.One, StopBits.Two, StopBits.OnePointFive];

    /// <summary>
    ///     可用校验方式列表
    /// </summary>
    public ObservableCollection<Parity> AvailableParity { get; } =
        [Parity.None, Parity.Odd, Parity.Even, Parity.Mark, Parity.Space];

    /// <summary>
    ///     保存配置命令
    /// </summary>
    public DelegateCommand SaveConfigurationCommand { get; }

    /// <summary>
    ///     刷新端口列表命令
    /// </summary>
    public ICommand RefreshPortsCommand { get; }

    /// <summary>
    ///     是否正在连接
    /// </summary>
    public bool IsPortOpen
    {
        get => _isPortOpen;
        set => SetProperty(ref _isPortOpen, value);
    }

    /// <summary>
    ///     加载配置
    /// </summary>
    private void LoadConfiguration()
    {
        try
        {
            var loadedSettings = _settingsService.LoadSettings<SerialPortSettings>();
            Settings = loadedSettings;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载串口设置时发生错误");
            _notificationService.ShowError("加载串口设置时发生错误");
        }
    }

    /// <summary>
    ///     保存配置
    /// </summary>
    private void SaveConfiguration()
    {
        try
        {
            _settingsService.SaveSettings(Settings);
            _notificationService.ShowSuccess("串口设置已保存");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存串口设置时发生错误");
            _notificationService.ShowError("保存串口设置时发生错误");
        }
    }

    /// <summary>
    ///     刷新可用端口列表
    /// </summary>
    private void RefreshPorts()
    {
        AvailablePorts.Clear();
        var ports = SerialPort.GetPortNames();
        foreach (var port in ports) AvailablePorts.Add(port);

        // 如果当前选择的端口不在可用列表中且有可用端口，则选择第一个可用端口
        if (!string.IsNullOrEmpty(Settings.PortName) && !AvailablePorts.Contains(Settings.PortName) &&
            AvailablePorts.Count > 0) Settings.PortName = AvailablePorts[0];
    }
}