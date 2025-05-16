using Common.Services.Settings;
using SowingWall.Models.Settings; // 引用我们刚创建的模型
using Serilog; // 日志记录

// For List<T>

namespace SowingWall.ViewModels.Settings
{
    public class ModbusTcpSettingsViewModel : BindableBase // 使用 Prism.Mvvm
    {
        private readonly ISettingsService _settingsService;
        private string _plcIpAddress;
        private int _plcPort;
        private byte _slaveId;

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

        public DelegateCommand SaveCommand { get; }

        public ModbusTcpSettingsViewModel(ISettingsService settingsService)
        { 
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            SaveCommand = new DelegateCommand(PerformSave, CanSave);
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

                Log.Information("SowingWall Modbus TCP 设置已成功保存: IP={Ip}, Port={Port}, SlaveId={SlaveId}",
                    settingsToSave.PlcIpAddress, settingsToSave.PlcPort, settingsToSave.SlaveId);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存 SowingWall Modbus TCP 设置时发生未知异常。");
            }
        }
    }
} 