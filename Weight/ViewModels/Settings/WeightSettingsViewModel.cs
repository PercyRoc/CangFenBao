using Common.Services.Settings;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Linq;
using Weight.Models.Settings;

namespace Weight.ViewModels.Settings;

public class WeightSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;
    private WeightSettings _currentSettings; // Make it non-readonly to allow re-assignment if needed

    private bool _isEnabled;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    private string _portName = string.Empty;

    public string PortName
    {
        get => _portName;
        set => SetProperty(ref _portName, value);
    }

    private int _baudRate;

    public int BaudRate
    {
        get => _baudRate;
        set => SetProperty(ref _baudRate, value);
    }

    private WeightType _selectedWeightType;

    public WeightType SelectedWeightType
    {
        get => _selectedWeightType;
        set => SetProperty(ref _selectedWeightType, value);
    }

    public List<LocalizedEnum<WeightType>> WeightTypes => WeightTypeExtension.AllTypes;

    private int _stableWeightSamples;

    public int StableWeightSamples
    {
        get => _stableWeightSamples;
        set => SetProperty(ref _stableWeightSamples, value);
    }

    private int _integrationTimeMs;

    public int IntegrationTimeMs
    {
        get => _integrationTimeMs;
        set => SetProperty(ref _integrationTimeMs, value);
    }

    // Properties for UI binding
    public ObservableCollection<string> AvailablePorts { get; }
    public ObservableCollection<int> AvailableBaudRates { get; }
    public DelegateCommand RefreshPortsCommand { get; }
    public DelegateCommand SaveCommand { get; }
    public DelegateCommand LoadCommand { get; } // Optional: Add a load/revert command

    public WeightSettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        _currentSettings = _settingsService.LoadSettings<WeightSettings>();
        
        AvailablePorts = new ObservableCollection<string>();
        AvailableBaudRates = new ObservableCollection<int> { 9600, 19200, 38400, 57600, 115200 }; // Common baud rates
        
        LoadSettingsToUi(_currentSettings);
        RefreshPorts(); // Initial population of ports

        RefreshPortsCommand = new DelegateCommand(RefreshPorts);
        SaveCommand = new DelegateCommand(OnSave);
        LoadCommand = new DelegateCommand(OnLoad); // Initialize load command
    }

    private void LoadSettingsToUi(WeightSettings settings)
    {
        IsEnabled = settings.IsEnabled;
        PortName = settings.PortName;
        BaudRate = settings.BaudRate;
        SelectedWeightType = settings.WeightType;
        StableWeightSamples = settings.StableWeightSamples;
        IntegrationTimeMs = settings.IntegrationTimeMs;
    }

    private void OnSave()
    {
        _currentSettings.IsEnabled = IsEnabled;
        _currentSettings.PortName = PortName;
        _currentSettings.BaudRate = BaudRate;
        _currentSettings.WeightType = SelectedWeightType;
        _currentSettings.StableWeightSamples = StableWeightSamples;
        _currentSettings.IntegrationTimeMs = IntegrationTimeMs;
        _settingsService.SaveSettings(_currentSettings, validate: true, throwOnError: false);
        // Optionally, provide user feedback e.g., via a status bar message or a toast notification.
    }

    private void OnLoad()
    {
        _currentSettings = _settingsService.LoadSettings<WeightSettings>();
        LoadSettingsToUi(_currentSettings);
        // Optionally, provide user feedback.
    }

    private void RefreshPorts()
    {
        string? previouslySelectedPort = PortName; // Store currently selected port
        AvailablePorts.Clear();
        try
        {
            var portNames = SerialPort.GetPortNames();
            foreach (var port in portNames.OrderBy(p => p))
            {
                AvailablePorts.Add(port);
            }

            // Restore selection if the port still exists, or select the first available if not.
            if (!string.IsNullOrEmpty(previouslySelectedPort) && AvailablePorts.Contains(previouslySelectedPort))
            {
                PortName = previouslySelectedPort;
            }
            else if (AvailablePorts.Any())
            {
                PortName = AvailablePorts.First();
            }
            else
            {
                PortName = string.Empty; // No ports available
            }
        }
        catch (Exception ex) // System.ComponentModel.Win32Exception might occur if no ports
        {
            Serilog.Log.Error(ex, "刷新串口列表失败");
            PortName = string.Empty; // Clear selection on error
        }
    }
}