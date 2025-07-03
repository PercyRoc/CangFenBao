using Serilog;
using SowingSorting.Models.Settings;
using SowingSorting.Services;
using Common.Services.Settings;
using Common.Events;
using Prism.Events;

namespace SowingSorting.ViewModels.Settings
{
    public class ModbusTcpSettingsViewModel : BindableBase
    {
        private readonly IModbusTcpService _modbusTcpService;
        private readonly ISettingsService _settingsService;
        private readonly IEventAggregator _eventAggregator;
        private const string SettingsKey = "SowingSorting.ModbusTcp";

        private ModbusTcpSettings _settings = new();
        public ModbusTcpSettings Settings
        {
            get => _settings;
                        private set
            {
                // 移除旧对象的事件监听
                if (_settings != null)
                {
                    _settings.PropertyChanged -= OnSettingsPropertyChanged;
                }
                
                if (!SetProperty(ref _settings, value)) return;
                
                // 为新对象添加事件监听
                if (_settings != null)
                {
                    _settings.PropertyChanged += OnSettingsPropertyChanged;
                }
                
                // 只有在命令已经创建后才调用RaiseCanExecuteChanged
                SaveCommand?.RaiseCanExecuteChanged();
                TestWriteRegisterCommand?.RaiseCanExecuteChanged();
            }
        }

        private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // 只有在命令已经创建后才调用RaiseCanExecuteChanged
            SaveCommand?.RaiseCanExecuteChanged();
            TestWriteRegisterCommand?.RaiseCanExecuteChanged();
        }

        private string _operationStatusMessage = "准备就绪";
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

        public ModbusTcpSettingsViewModel(IModbusTcpService modbusTcpService, ISettingsService settingsService, IEventAggregator eventAggregator)
        {
            _modbusTcpService = modbusTcpService ?? throw new ArgumentNullException(nameof(modbusTcpService));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            
            // 先初始化设置，再创建命令
            OnLoad();
            
            SaveCommand = new DelegateCommand(OnSave, CanSaveSettings);
            TestWriteRegisterCommand = new DelegateCommand(async void () => await TestWriteRegisterAsync(), CanTestWriteRegister);
            
            // 确保命令状态在初始化后立即更新
            SaveCommand.RaiseCanExecuteChanged();
            TestWriteRegisterCommand.RaiseCanExecuteChanged();
        }
        
        private bool CanSaveSettings()
        {
            if (Settings == null)
                return false;
                
            if (string.IsNullOrWhiteSpace(Settings.IpAddress) || 
                Settings.Port <= 0 || Settings.Port > 65535)
            {
                return false;
            }

            // 验证异常格口配置
            if (!ValidateExceptionChutes(Settings.ExceptionChuteNumbers))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// 验证异常格口配置是否有效
        /// </summary>
        /// <param name="exceptionChuteNumbers">异常格口配置字符串</param>
        /// <returns>是否有效</returns>
        private bool ValidateExceptionChutes(string exceptionChuteNumbers)
        {
            if (string.IsNullOrWhiteSpace(exceptionChuteNumbers))
            {
                return false;
            }

            var chuteNumbers = exceptionChuteNumbers.Split(';', StringSplitOptions.RemoveEmptyEntries);
            if (chuteNumbers.Length == 0)
            {
                return false;
            }

            foreach (var chuteStr in chuteNumbers)
            {
                if (!int.TryParse(chuteStr.Trim(), out int chuteNumber) || chuteNumber <= 0)
                {
                    return false;
                }
            }

            return true;
        }

        private bool CanTestWriteRegister()
        {
            if (Settings == null)
                return false;
                
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
                if (loadedSettings != null)
                {
                    Settings = loadedSettings;
                    Log.Information("Modbus TCP 设置已从 ISettingsService 加载 (Key: {Key})。", SettingsKey);
                }
                else
                {
                    Log.Warning("从 ISettingsService 加载的设置为空，使用默认设置。");
                    Settings = new ModbusTcpSettings();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "从 ISettingsService 加载 Modbus TCP 设置失败 (Key: {Key})。将使用默认设置。", SettingsKey);
                Settings = new ModbusTcpSettings(); 
            }
            
            // 确保Settings不为null
            Settings ??= new ModbusTcpSettings();
            
            OperationStatusMessage = "设置已加载。";
        }

        private void OnSave()
        {
            if (Settings == null)
            {
                OperationStatusMessage = "无法保存：设置对象未初始化。";
                return;
            }
            
            // 详细验证并提供具体错误信息
            if (string.IsNullOrWhiteSpace(Settings.IpAddress))
            {
                OperationStatusMessage = "无法保存：IP地址不能为空。";
                return;
            }

            if (Settings.Port <= 0 || Settings.Port > 65535)
            {
                OperationStatusMessage = "无法保存：端口号必须在1-65535范围内。";
                return;
            }

            if (!ValidateExceptionChutes(Settings.ExceptionChuteNumbers))
            {
                OperationStatusMessage = "无法保存：异常格口配置无效。请输入有效的格口号，多个格口用分号分割。";
                return;
            }

            Log.Information("尝试将 Modbus TCP 设置通过 ISettingsService 保存 (Key: {Key})", SettingsKey);
            try
            {
                _settingsService.SaveSettings(Settings, validate: true, throwOnError: false);
                Log.Information("Modbus TCP 设置已通过 ISettingsService 保存 (Key: {Key})", SettingsKey);
                OperationStatusMessage = $"设置已保存。格口数量：{Settings.ChuteCount}，异常格口：{Settings.ExceptionChuteNumbers}";
                
                // 发送格口配置更改事件，通知MainViewModel刷新格口
                _eventAggregator.GetEvent<ChuteConfigurationChangedEvent>().Publish();
                Log.Information("格口配置更改事件已发送");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "通过 ISettingsService 保存 Modbus TCP 设置失败 (Key: {Key})", SettingsKey);
                OperationStatusMessage = "设置保存失败！请检查配置项。";
            }
        }

        private async Task TestWriteRegisterAsync()
        {
            if (Settings == null)
            {
                OperationStatusMessage = "无法执行测试：设置对象未初始化。";
                return;
            }
            
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