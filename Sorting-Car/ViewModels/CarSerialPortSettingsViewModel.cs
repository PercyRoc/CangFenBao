using Common.Services.Settings;
using Sorting_Car.Models;
using System.IO.Ports;
using System.Collections.ObjectModel;
using Serilog;
using Sorting_Car.Resources;

namespace Sorting_Car.ViewModels
{
    public class CarSerialPortSettingsViewModel : BindableBase
    {
        private readonly ISettingsService _settingsService;
        private CarSerialPortSettings _settings;

        public string Title => Strings.CarSerialPortSettings_Title;
        public event Action<IDialogResult> RequestClose;

        public CarSerialPortSettings Settings
        {
            get => _settings;
            set => SetProperty(ref _settings, value);
        }

        public ObservableCollection<string> AvailablePortNames { get; }
        public ObservableCollection<int> AvailableBaudRates { get; }
        public Array ParityValues => Enum.GetValues(typeof(SerialParity));
        public Array StopBitsValues => Enum.GetValues(typeof(SerialStopBits));
        public ObservableCollection<int> AvailableDataBits { get; }


        public DelegateCommand SaveCommand { get; }
        public DelegateCommand CloseCommand { get; } // For closing dialog


        public CarSerialPortSettingsViewModel(ISettingsService settingsService)
        {
            _settingsService = settingsService;
            Settings = _settingsService.LoadSettings<CarSerialPortSettings>();
            AvailablePortNames = [];
            try
            {
                var portNames = SerialPort.GetPortNames();
                foreach (var portName in portNames.OrderBy(p => p))
                {
                    AvailablePortNames.Add(portName);
                }
            }
            catch (Exception ex) 
            {
                Log.Error(ex, "获取可用串口列表失败");
            }

            if (AvailablePortNames.Count == 0)
            {
                 AvailablePortNames.Add("COM1"); // 如果没有可用串口，添加一个默认的
            }
            
           
            if (!string.IsNullOrEmpty(Settings.PortName) && !AvailablePortNames.Contains(Settings.PortName))
            {
               
            }
            else if (string.IsNullOrEmpty(Settings.PortName) && AvailablePortNames.Any())
            {
                Settings.PortName = AvailablePortNames[0];
            }


            AvailableBaudRates = [9600, 14400, 19200, 38400, 57600, 115200, 128000];
            AvailableDataBits = [7, 8];

            SaveCommand = new DelegateCommand(PerformSaveSettings);
            CloseCommand = new DelegateCommand(PerformClose);
        }

        private void PerformSaveSettings()
        {
            Log.Information("Attempting to save CarSerialPortSettings. PortName before save: {PortName}", Settings.PortName);
            try
            {
                _settingsService.SaveSettings(Settings, throwOnError: true); 
                Log.Information("CarSerialPortSettings saved successfully. PortName after save: {PortName}", Settings.PortName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save CarSerialPortSettings");
                // Optionally, inform the user via a dialog or message
                // For example, if IDialogService is available:
                // _dialogService.ShowNotification("Failed to save settings: " + ex.Message);
            }
        }
        
        private void PerformClose()
        {
            RequestClose?.Invoke(new DialogResult(ButtonResult.Cancel));
        }
    }
} 