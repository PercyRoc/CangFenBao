using Serilog;
using SowingSorting.Models.Settings;
using SowingSorting.Services;
using Common.Services.Settings;

namespace SowingSorting.ViewModels.Settings
{
    public class ModbusTcpSettingsViewModel : BindableBase
    {
        private readonly IModbusTcpService _modbusTcpService;
        private readonly ISettingsService _settingsService;
        private const string SettingsKey = "SowingSorting.ModbusTcp";

        private ModbusTcpSettings _settings = null!;
        public ModbusTcpSettings Settings
        {
            get => _settings;
            private set
            {
                if (!SetProperty(ref _settings, value)) return;
                
                SaveCommand.RaiseCanExecuteChanged();
                TestWriteRegisterCommand.RaiseCanExecuteChanged();
            }
        }

        private string _operationStatusMessage = null!;
        public string OperationStatusMessage
        {
            get => _operationStatusMessage;
            private set => SetProperty(ref _operationStatusMessage, value);
        }

        private int _testRegisterValue;
        public int TestRegisterValue
        {
            get => _testRegisterValue;
            set => SetProperty(ref _testRegisterValue, value);
        }

        public DelegateCommand SaveCommand { get; }
        public DelegateCommand TestWriteRegisterCommand { get; }

        public ModbusTcpSettingsViewModel(IModbusTcpService modbusTcpService, ISettingsService settingsService)
        {
            _modbusTcpService = modbusTcpService ?? throw new ArgumentNullException(nameof(modbusTcpService));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            
            SaveCommand = new DelegateCommand(OnSave, CanSaveSettings);
            TestWriteRegisterCommand = new DelegateCommand(async void () => await TestWriteRegisterAsync(), CanTestWriteRegister);
            
            OnLoad();
        }
        
        private bool CanSaveSettings()
        {
            return !string.IsNullOrWhiteSpace(Settings.IpAddress) && 
                   Settings.Port is > 0 and <= 65535;
        }

        private bool CanTestWriteRegister()
        {
            return !string.IsNullOrWhiteSpace(Settings.IpAddress) && 
                   Settings.Port is > 0 and <= 65535 &&
                   _modbusTcpService.IsConnected;
        }

        private void OnLoad()
        {
            Log.Information("尝试从 ISettingsService 加载 Modbus TCP 设置 (Key: {Key})。", SettingsKey);
            try
            {
                var loadedSettings = _settingsService.LoadSettings<ModbusTcpSettings>(SettingsKey);
                Settings = loadedSettings;
                Log.Information("Modbus TCP 设置已从 ISettingsService 加载 (Key: {Key})。", SettingsKey);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "从 ISettingsService 加载 Modbus TCP 设置失败 (Key: {Key})。将使用默认设置。", SettingsKey);
                Settings = new ModbusTcpSettings(); 
            }
            OperationStatusMessage = "设置已加载。";
        }

        private void OnSave()
        {
            if (!CanSaveSettings())
            {
                OperationStatusMessage = "无法保存：设置无效。";
                return;
            }

            Log.Information("尝试将 Modbus TCP 设置通过 ISettingsService 保存 (Key: {Key})", SettingsKey);
            try
            {
                _settingsService.SaveSettings(Settings, validate: true, throwOnError: false);
                Log.Information("Modbus TCP 设置已通过 ISettingsService 保存 (Key: {Key})", SettingsKey);
                OperationStatusMessage = "设置已保存。";
            }
            catch (Exception ex)
            {
                Log.Error(ex, "通过 ISettingsService 保存 Modbus TCP 设置失败 (Key: {Key})", SettingsKey);
                OperationStatusMessage = "设置保存失败！";
            }
        }

        private async Task TestWriteRegisterAsync()
        {
            if (!_modbusTcpService.IsConnected)
            {
                OperationStatusMessage = $"Modbus 服务未连接。尝试自动连接到 {Settings.IpAddress}:{Settings.Port}...";
                Log.Warning("尝试写入寄存器但服务未连接。IP: {IP}, Port: {Port}", Settings.IpAddress, Settings.Port);
                bool connected = await _modbusTcpService.ConnectAsync(Settings);
                if (!connected)
                {
                    OperationStatusMessage = $"自动连接到 {Settings.IpAddress}:{Settings.Port} 失败。请检查设置。";
                    Log.Error("写入前的自动连接失败。");
                    TestWriteRegisterCommand.RaiseCanExecuteChanged(); 
                    return;
                }
                Log.Information("写入前的自动连接成功。");
                OperationStatusMessage = $"已连接到 {Settings.IpAddress}:{Settings.Port}。请重试写入操作。";
                TestWriteRegisterCommand.RaiseCanExecuteChanged(); 
                return; 
            }
            
            if (!CanTestWriteRegister()) 
            {
                OperationStatusMessage = "请检查 IP 地址和端口设置。";
                return;
            }

            int address = Settings.DefaultRegisterAddress;
            int value = TestRegisterValue;
            OperationStatusMessage = $"正在向默认寄存器地址 {address} 写入值 {value} 到 {Settings.IpAddress}:{Settings.Port}...";
            Log.Information("开始写入: IP={IpAddress}, Port={Port}, Address={Address}, Value={Value}", 
                            Settings.IpAddress, Settings.Port, address, value);
            
            bool success = await _modbusTcpService.WriteSingleRegisterAsync(address, value);
            if (success)
            {
                OperationStatusMessage = $"成功向默认寄存器地址 {address} 写入值 {value}.";
                Log.Information("写入成功。");
            }
            else
            {
                OperationStatusMessage = $"写入默认寄存器地址 {address} 失败.";
                Log.Error("写入失败。");
            }
        }
    }
} 