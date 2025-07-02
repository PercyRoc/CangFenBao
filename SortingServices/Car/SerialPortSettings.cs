using System.IO.Ports;
using Common.Services.Settings;

namespace SortingServices.Car;

/// <summary>
///     串口通讯设置
/// </summary>
[Configuration("SerialPortSettings")]
public class SerialPortSettings : BindableBase
{
    private int _baudRate = 9600;
    private int _commandDelayMs;
    private int _dataBits = 8;
    private bool _dtrEnable;
    private Parity _parity = Parity.None;
    private string _portName = "COM1";
    private int _readTimeout = 500;
    private bool _rtsEnable;
    private StopBits _stopBits = StopBits.One;
    private int _writeTimeout = 500;

    /// <summary>
    ///     端口名称
    /// </summary>
    public string PortName
    {
        get => _portName;
        set => SetProperty(ref _portName, value);
    }

    /// <summary>
    ///     波特率
    /// </summary>
    public int BaudRate
    {
        get => _baudRate;
        set => SetProperty(ref _baudRate, value);
    }

    /// <summary>
    ///     数据位
    /// </summary>
    public int DataBits
    {
        get => _dataBits;
        set => SetProperty(ref _dataBits, value);
    }

    /// <summary>
    ///     停止位
    /// </summary>
    public StopBits StopBits
    {
        get => _stopBits;
        set => SetProperty(ref _stopBits, value);
    }

    /// <summary>
    ///     校验方式
    /// </summary>
    public Parity Parity
    {
        get => _parity;
        set => SetProperty(ref _parity, value);
    }

    /// <summary>
    ///     是否启用请求发送信号(RTS)
    /// </summary>
    public bool RtsEnable
    {
        get => _rtsEnable;
        set => SetProperty(ref _rtsEnable, value);
    }

    /// <summary>
    ///     是否启用数据终端就绪信号(DTR)
    /// </summary>
    public bool DtrEnable
    {
        get => _dtrEnable;
        set => SetProperty(ref _dtrEnable, value);
    }

    /// <summary>
    ///     读取超时时间(毫秒)
    /// </summary>
    public int ReadTimeout
    {
        get => _readTimeout;
        set => SetProperty(ref _readTimeout, value);
    }

    /// <summary>
    ///     写入超时时间(毫秒)
    /// </summary>
    public int WriteTimeout
    {
        get => _writeTimeout;
        set => SetProperty(ref _writeTimeout, value);
    }

    /// <summary>
    ///     命令延迟发送时间(毫秒)，命令将在触发后等待此时间后再发送
    /// </summary>
    public int CommandDelayMs
    {
        get => _commandDelayMs;
        set => SetProperty(ref _commandDelayMs, value);
    }
}