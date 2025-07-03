using Common.Services.Settings;


namespace Sorting_Car.Models
{
    /// <summary>
    /// 串口校验方式
    /// </summary>
    public enum SerialParity
    {
        None,
        Odd,
        Even,
        Mark,
        Space
    }

    /// <summary>
    /// 串口停止位
    /// </summary>
    public enum SerialStopBits
    {
        None,
        One,
        Two,
        OnePointFive
    }

    /// <summary>
    /// 小车串口连接设置
    /// </summary>
    [Configuration("CarSerialPortSettings")]
    public class CarSerialPortSettings : BindableBase
    {
        private string _portName = "COM1";
        private int _baudRate = 9600;
        private int _dataBits = 8;
        private SerialParity _parity = SerialParity.None;
        private SerialStopBits _stopBits = SerialStopBits.One;
        private int _commandDelayMs;

        /// <summary>
        /// 端口名称
        /// </summary>
        public string PortName
        {
            get => _portName;
            set => SetProperty(ref _portName, value);
        }

        /// <summary>
        /// 波特率
        /// </summary>
        public int BaudRate
        {
            get => _baudRate;
            set => SetProperty(ref _baudRate, value);
        }

        /// <summary>
        /// 数据位
        /// </summary>
        public int DataBits
        {
            get => _dataBits;
            set => SetProperty(ref _dataBits, value);
        }

        /// <summary>
        /// 校验位
        /// </summary>
        public SerialParity Parity
        {
            get => _parity;
            set => SetProperty(ref _parity, value);
        }

        /// <summary>
        /// 停止位
        /// </summary>
        public SerialStopBits StopBits
        {
            get => _stopBits;
            set => SetProperty(ref _stopBits, value);
        }

        /// <summary>
        /// 命令延迟（毫秒）
        /// </summary>
        public int CommandDelayMs
        {
            get => _commandDelayMs;
            set => SetProperty(ref _commandDelayMs, value);
        }
    }
} 