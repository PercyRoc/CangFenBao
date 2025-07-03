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

        private int _defaultRegisterAddress;
        /// <summary>
        /// 默认寄存器地址
        /// </summary>
        public int DefaultRegisterAddress
        {
            get => _defaultRegisterAddress;
            set => SetProperty(ref _defaultRegisterAddress, value);
        }

        private int _chuteCount = 60;
        /// <summary>
        /// 格口数量
        /// </summary>
        public int ChuteCount
        {
            get => _chuteCount;
            set => SetProperty(ref _chuteCount, value);
        }

        private string _exceptionChuteNumbers = "61";
        /// <summary>
        /// 异常格口编号（多个格口用分号;分割，如：61;62;99）
        /// </summary>
        public string ExceptionChuteNumbers
        {
            get => _exceptionChuteNumbers;
            set => SetProperty(ref _exceptionChuteNumbers, value);
        }
    }
} 