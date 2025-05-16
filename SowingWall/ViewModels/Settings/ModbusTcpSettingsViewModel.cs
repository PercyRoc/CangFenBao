using Common.Services.Settings;
using SowingWall.Models.Settings; // 引用我们刚创建的模型
using Serilog; // 日志记录
using SowingWall.Services;
using System.Threading.Tasks;
using System; // Required for ArgumentNullException

// For List<T>

namespace SowingWall.ViewModels.Settings
{
    public class ModbusTcpSettingsViewModel : BindableBase // 使用 Prism.Mvvm
    {
        private readonly ISettingsService _settingsService;
        private readonly ISowingWallPlcService _sowingWallPlcService; // 新增PLC服务
        private string _plcIpAddress;
        private int _plcPort;
        private byte _slaveId;

        // 新增测试相关属性
        private ushort _testRegisterAddress = 1; // 默认地址
        private ushort _testRegisterValue;   // 默认值
        private string _testFeedbackMessage = string.Empty;
        private bool _isTestingWrite;

        public string PlcIpAddress
        {
            get => _plcIpAddress;
            set => SetProperty(ref _plcIpAddress, value);
        }

        public int PlcPort
        {
            get => _plcPort;
            set => SetProperty(ref _plcPort, value);
        }

        public byte SlaveId
        {
            get => _slaveId;
            set => SetProperty(ref _slaveId, value);
        }

        // 测试属性的公开 Getter/Setter
        public ushort TestRegisterAddress
        {
            get => _testRegisterAddress;
            set => SetProperty(ref _testRegisterAddress, value, () => TestWriteCommand?.RaiseCanExecuteChanged());
        }

        public ushort TestRegisterValue
        {
            get => _testRegisterValue;
            set => SetProperty(ref _testRegisterValue, value, () => TestWriteCommand?.RaiseCanExecuteChanged());
        }

        public string TestFeedbackMessage
        {
            get => _testFeedbackMessage;
            set => SetProperty(ref _testFeedbackMessage, value);
        }

        public bool IsTestingWrite
        {
            get => _isTestingWrite;
            set => SetProperty(ref _isTestingWrite, value, () => TestWriteCommand?.RaiseCanExecuteChanged());
        }

        public DelegateCommand SaveCommand { get; }
        public DelegateCommand TestWriteCommand { get; } // 新增测试命令

        public ModbusTcpSettingsViewModel(ISettingsService settingsService, ISowingWallPlcService sowingWallPlcService) // 注入PLC服务
        { 
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _sowingWallPlcService = sowingWallPlcService ?? throw new ArgumentNullException(nameof(sowingWallPlcService)); // 保存PLC服务实例
            
            SaveCommand = new DelegateCommand(PerformSave, CanSave);
            TestWriteCommand = new DelegateCommand(PerformTestWriteAsync, CanTestWrite); // 初始化测试命令
            LoadSettings();
        }

        private void LoadSettings()
        {
            Log.Debug("加载 SowingWall Modbus TCP 设置...");
            var settings = _settingsService.LoadSettings<ModbusTcpSettings>();
            PlcIpAddress = settings.PlcIpAddress;
            PlcPort = settings.PlcPort;
            SlaveId = settings.SlaveId;
            Log.Information("SowingWall Modbus TCP 设置已加载: IP={Ip}, Port={Port}, SlaveId={SlaveId}", PlcIpAddress, PlcPort, SlaveId);
        }

        private bool CanSave()
        {
            // 可以添加验证逻辑，例如IP地址格式等
            return true;
        }

        private void PerformSave()
        {
            Log.Debug("准备保存 SowingWall Modbus TCP 设置...");
            var settingsToSave = new ModbusTcpSettings
            {
                PlcIpAddress = this.PlcIpAddress,
                PlcPort = this.PlcPort,
                SlaveId = this.SlaveId
            };

            try
            {
                _settingsService.SaveSettings(settingsToSave, validate: false, throwOnError: false);
                // 可选：通知用户保存成功
                TestFeedbackMessage = "设置已保存。"; 
                Log.Information("SowingWall Modbus TCP 设置已成功保存: IP={Ip}, Port={Port}, SlaveId={SlaveId}",
                    settingsToSave.PlcIpAddress, settingsToSave.PlcPort, settingsToSave.SlaveId);
            }
            catch (Exception ex)
            {
                TestFeedbackMessage = $"保存设置失败: {ex.Message}";
                Log.Error(ex, "保存 SowingWall Modbus TCP 设置时发生未知异常。");
            }
        }

        // 测试写入命令相关方法
        private bool CanTestWrite()
        {
            return !IsTestingWrite; // 如果不在测试中，则可以执行
        }

        private async void PerformTestWriteAsync() // Prism DelegateCommand 可以处理 async void
        {
            if (!CanTestWrite()) return;

            IsTestingWrite = true;
            TestFeedbackMessage = "正在尝试写入...";
            Log.Information("开始测试写入: 地址={Address}, 值={Value}", TestRegisterAddress, TestRegisterValue);

            try
            {
                // 确保在测试前，PLC服务使用了最新的（可能刚保存的）配置
                // SowingWallPlcService.ConnectAsync()内部已修改为会调用LoadSettings()
                
                bool connected = await _sowingWallPlcService.ConnectAsync();
                if (connected)
                {
                    Log.Debug("PLC连接成功，尝试写入寄存器。");
                    await _sowingWallPlcService.WriteSingleRegisterAsync(TestRegisterAddress, TestRegisterValue);
                    TestFeedbackMessage = $"成功写入值 {TestRegisterValue} 到寄存器 {TestRegisterAddress}。";
                    Log.Information("测试写入成功: 地址={Address}, 值={Value}", TestRegisterAddress, TestRegisterValue);
                }
                else
                {
                    TestFeedbackMessage = "连接PLC失败。请检查IP、端口设置，并确保PLC在线。";
                    Log.Warning("测试写入失败: PLC连接失败。");
                }
            }
            catch (ModbusException mex)
            {
                TestFeedbackMessage = $"Modbus写入失败: {mex.Message} (功能码: {mex.FunctionCode}, 异常码: {mex.ExceptionCode})";
                Log.Error(mex, "测试写入时发生Modbus异常: 地址={Address}, 值={Value}", TestRegisterAddress, TestRegisterValue);
            }
            catch (TimeoutException tex)
            {
                 TestFeedbackMessage = $"写入超时: {tex.Message}";
                 Log.Error(tex, "测试写入超时: 地址={Address}, 值={Value}", TestRegisterAddress, TestRegisterValue);
            }
            catch (Exception ex)
            {
                TestFeedbackMessage = $"写入失败: {ex.Message}";
                Log.Error(ex, "测试写入时发生未知异常: 地址={Address}, 值={Value}", TestRegisterAddress, TestRegisterValue);
            }
            finally
            {
                IsTestingWrite = false;
            }
        }
    }
} 