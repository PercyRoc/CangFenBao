using System.Collections.ObjectModel;
using System.IO.Ports;
using Common.Services.Settings;
using Common.Services.Ui;
using DeviceService.DataSourceDevices.Weight;
using Serilog;

namespace SharedUI.ViewModels.Settings;

public class ChineseWeightSettingsViewModel : BindableBase
{
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;
    private WeightSettings _configuration = null!;

    public ChineseWeightSettingsViewModel(
        ISettingsService settingsService,
        INotificationService notificationService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;

        RefreshPortsCommand = new DelegateCommand(ExecuteRefreshPorts);
        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);

        // 加载配置
        LoadSettings();

        // 初始化可用串口
        ExecuteRefreshPorts();
    }

    public WeightSettings Configuration
    {
        get => _configuration;
        private set => SetProperty(ref _configuration, value);
    }

    public static Array WeightTypes
    {
        get => Enum.GetValues(typeof(WeightType));
    }
    public static int[] BaudRates
    {
        get => [4800, 9600, 19200, 38400, 57600, 115200];
    }
    public static int[] DataBits
    {
        get => [7, 8];
    }
    public static Array StopBitOptions
    {
        get => Enum.GetValues(typeof(StopBits));
    }
    public static Array ParityOptions
    {
        get => Enum.GetValues(typeof(Parity));
    }
    public ObservableCollection<string> PortNames { get; } = [];

    public DelegateCommand RefreshPortsCommand { get; }
    public DelegateCommand SaveConfigurationCommand { get; }

    private void ExecuteRefreshPorts()
    {
        try
        {
            PortNames.Clear();
            var ports = SerialPort.GetPortNames();
            foreach (var port in ports) PortNames.Add(port);

            if (PortNames.Count == 0)
                _notificationService.ShowWarningWithToken("没有找到可用的串口", "SettingWindowGrowl");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "刷新串口列表失败");
            _notificationService.ShowErrorWithToken($"刷新串口列表失败: {ex.Message}",
                "SettingWindowGrowl");
        }
    }

    private void ExecuteSaveConfiguration()
    {
        _settingsService.SaveSettings(Configuration);
        _notificationService.ShowSuccessWithToken("重量设置已保存", "SettingWindowGrowl");
    }

    private void LoadSettings()
    {
        Configuration = _settingsService.LoadSettings<WeightSettings>();
    }
}