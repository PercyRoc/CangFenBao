using System.IO.Ports;

namespace DeviceService.DataSourceDevices.Weight;

/// <summary>
///     串口参数
/// </summary>
public class SerialPortParams
{
    /// <summary>
    ///     串口名称
    /// </summary>
    public string PortName { get; set; } = string.Empty;

    /// <summary>
    ///     波特率
    /// </summary>
    public int BaudRate { get; set; } = 9600;

    /// <summary>
    ///     数据位
    /// </summary>
    public int DataBits { get; set; } = 8;

    /// <summary>
    ///     停止位
    /// </summary>
    public StopBits StopBits { get; set; } = StopBits.One;

    /// <summary>
    ///     校验位
    /// </summary>
    public Parity Parity { get; set; } = Parity.None;
}