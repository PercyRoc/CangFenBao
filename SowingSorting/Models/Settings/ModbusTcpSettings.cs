using Prism.Mvvm;
using Common.Services.Settings;

namespace SowingSorting.Models.Settings
{
    /// <summary>
    /// Modbus TCP 连接设置
    /// </summary>
    [Configuration("SowingSorting.ModbusTcp")]
    public class ModbusTcpSettings : BindableBase
    {
        private string _ipAddress = "127.0.0.1";
        /// <summary>
        /// IP 地址
        /// </summary>
        public string IpAddress
        {
            get => _ipAddress;
            set => SetProperty(ref _ipAddress, value);
        }

        private int _port = 502;
        /// <summary>
        /// 端口号
        /// </summary>
        public int Port
        {
            get => _port;
            set => SetProperty(ref _port, value);
        }

        private int _connectionTimeout = 1000; // 毫秒
        /// <summary>
        /// 连接超时时间 (毫秒)
        /// </summary>
        public int ConnectionTimeout
        {
            get => _connectionTimeout;
            set => SetProperty(ref _connectionTimeout, value);
        }

        private int _defaultRegisterAddress = 0;
        /// <summary>
        /// 默认寄存器地址
        /// </summary>
        public int DefaultRegisterAddress
        {
            get => _defaultRegisterAddress;
            set => SetProperty(ref _defaultRegisterAddress, value);
        }
    }
} 