using System.Collections.ObjectModel;
using System.IO.Ports;
using Common.Services.Settings;
using Common.Services.Ui;
using DeviceService.DataSourceDevices.Weight;
using Serilog;

namespace XinBa.ViewModels.Settings;

public class WeightSettingsViewModel : BindableBase
{
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;
    private WeightSettings _configuration = null!;

    public WeightSettingsViewModel(
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

    public static Array WeightTypes => Enum.GetValues(typeof(WeightType));
    public static int[] BaudRates => [4800, 9600, 19200, 38400, 57600, 115200];
    public static int[] DataBits => [7, 8];
    public static Array StopBitOptions => Enum.GetValues(typeof(StopBits));
    public static Array ParityOptions => Enum.GetValues(typeof(Parity));
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
                _notificationService.ShowWarningWithToken("No available serial ports found", "SettingWindowGrowl");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to refresh serial ports");
            _notificationService.ShowErrorWithToken($"Failed to refresh serial ports: {ex.Message}",
                "SettingWindowGrowl");
        }
    }

    private void ExecuteSaveConfiguration()
    {
        _settingsService.SaveSettings(Configuration);
        _notificationService.ShowSuccessWithToken("Weight settings saved", "SettingWindowGrowl");
    }

    private void LoadSettings()
    {
        Configuration = _settingsService.LoadSettings<WeightSettings>();
    }
}