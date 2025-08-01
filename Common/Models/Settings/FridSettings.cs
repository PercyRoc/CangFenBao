using Common.Services.Settings;
using Prism.Mvvm;
using System.ComponentModel;

namespace Common.Models.Settings;

/// <summary>
/// Frid连接类型枚举
/// </summary>
public enum FridConnectionType
{
    [Description("TCP连接")]
    Tcp,

    [Description("串口连接")]
    SerialPort
}

/// <summary>
/// Frid设备设置
/// </summary>
[Configuration("FridSettings")]
public class FridSettings : BindableBase
{
    private FridConnectionType _connectionType = FridConnectionType.Tcp;
    private string _tcpIpAddress = "127.0.0.1";
    private int _tcpPort = 8080;
    private string _serialPortName = "COM1";
    private int _baudRate = 115200;
    private int _dataBits = 8;
    private int _stopBits = 1;
    private int _parity = 0; // 0=无，1=奇，2=偶
    private int _power = 30; // 功率设置，单位dBm
    private bool _isEnabled = true;

    /// <summary>
    /// 是否启用Frid设备
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    /// <summary>
    /// 连接类型
    /// </summary>
    public FridConnectionType ConnectionType
    {
        get => _connectionType;
        set => SetProperty(ref _connectionType, value);
    }

    /// <summary>
    /// TCP连接IP地址
    /// </summary>
    public string TcpIpAddress
    {
        get => _tcpIpAddress;
        set => SetProperty(ref _tcpIpAddress, value);
    }

    /// <summary>
    /// TCP连接端口
    /// </summary>
    public int TcpPort
    {
        get => _tcpPort;
        set => SetProperty(ref _tcpPort, value);
    }

    /// <summary>
    /// 串口名称
    /// </summary>
    public string SerialPortName
    {
        get => _serialPortName;
        set => SetProperty(ref _serialPortName, value);
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
    /// 停止位
    /// </summary>
    public int StopBits
    {
        get => _stopBits;
        set => SetProperty(ref _stopBits, value);
    }

    /// <summary>
    /// 校验位（0=无，1=奇，2=偶）
    /// </summary>
    public int Parity
    {
        get => _parity;
        set => SetProperty(ref _parity, value);
    }

    /// <summary>
    /// 功率设置（dBm）
    /// </summary>
    public int Power
    {
        get => _power;
        set => SetProperty(ref _power, value);
    }
} 