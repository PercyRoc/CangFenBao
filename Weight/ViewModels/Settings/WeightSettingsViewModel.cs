using Common.Services.Settings;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Linq;
using Weight.Models.Settings;

namespace Weight.ViewModels.Settings;

public class WeightSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;
    
    private WeightSettings _settings;
    public WeightSettings Settings
    {
        get => _settings;
        set => SetProperty(ref _settings, value);
    }

    // Properties for UI binding - these remain as they are not direct mirrors of Settings properties
    public ObservableCollection<string> AvailablePorts { get; }
    public ObservableCollection<int> AvailableBaudRates { get; }
    public List<LocalizedEnum<WeightType>> WeightTypes => WeightTypeExtension.AllTypes;

    public DelegateCommand RefreshPortsCommand { get; }
    public DelegateCommand SaveCommand { get; }
    public DelegateCommand LoadCommand { get; }

    public WeightSettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        Settings = _settingsService.LoadSettings<WeightSettings>();
        
        AvailablePorts = [];
        AvailableBaudRates = [9600, 19200, 38400, 57600, 115200]; // Common baud rates
        
        RefreshPorts();

        RefreshPortsCommand = new DelegateCommand(RefreshPorts);
        SaveCommand = new DelegateCommand(OnSave);
        LoadCommand = new DelegateCommand(OnLoad);
    }

    private void OnSave()
    {
        // Properties are now directly bound to Settings object, which itself notifies changes.
        _settingsService.SaveSettings(Settings, validate: true, throwOnError: false);
        // 可选，通过状态栏消息或Toast通知提供用户反馈。
    }

    private void OnLoad()
    {
        Settings = _settingsService.LoadSettings<WeightSettings>();
        RefreshPorts(); // Refresh ports as PortName might have changed.
        // 可选，通过状态栏消息或Toast通知提供用户反馈。
    }

    private void RefreshPorts()
    {
        string? previouslySelectedPort = Settings.PortName; // Use Settings.PortName
        AvailablePorts.Clear();
        try
        {
            var portNames = SerialPort.GetPortNames();
            foreach (var port in portNames.OrderBy(p => p))
            {
                AvailablePorts.Add(port);
            }

            if (!string.IsNullOrEmpty(previouslySelectedPort) && AvailablePorts.Contains(previouslySelectedPort))
            {
                Settings.PortName = previouslySelectedPort; // Set on Settings.PortName
            }
            else if (AvailablePorts.Any())
            {
                Settings.PortName = AvailablePorts.First(); // Set on Settings.PortName
            }
            else
            {
                Settings.PortName = string.Empty; // Set on Settings.PortName
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "刷新串口列表失败");
            Settings.PortName = string.Empty; // Set on Settings.PortName
        }
    }
}