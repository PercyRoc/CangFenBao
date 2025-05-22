using Common.Services.Settings;
using System.Collections.ObjectModel;
using System.IO.Ports;
using Weight.Models.Settings;

namespace Weight.ViewModels.Settings;

public class WeightSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;
    
    private WeightSettings _settings = null!;
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

    public WeightSettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        Settings = _settingsService.LoadSettings<WeightSettings>();
        
        AvailablePorts = [];
        AvailableBaudRates = [9600, 19200, 38400, 57600, 115200]; // Common baud rates
        
        RefreshPorts();

        RefreshPortsCommand = new DelegateCommand(RefreshPorts);
        SaveCommand = new DelegateCommand(OnSave);
    }

    private void OnSave()
    {
        _settingsService.SaveSettings(Settings, validate: true, throwOnError: false);
    }

    private void RefreshPorts()
    {
        string previouslySelectedPort = Settings.PortName;
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
                Settings.PortName = previouslySelectedPort;
            }
            else if (AvailablePorts.Any())
            {
                Settings.PortName = AvailablePorts.First();
            }
            else
            {
                Settings.PortName = string.Empty;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "刷新串口列表失败");
            Settings.PortName = string.Empty;
        }
    }
}