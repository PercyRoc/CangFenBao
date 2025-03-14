using System.IO.Ports;

namespace Presentation_BenFly.Services.Belt;

/// <summary>
///     皮带串口服务接口
/// </summary>
public interface IBeltSerialService : IDisposable
{
    /// <summary>
    ///     串口是否已打开
    /// </summary>
    bool IsOpen { get; }

    /// <summary>
    ///     串口状态变更事件
    /// </summary>
    event EventHandler<bool> ConnectionStatusChanged;

    /// <summary>
    ///     打开串口
    /// </summary>
    /// <param name="settings">串口设置</param>
    void Open(SerialPort settings);

    /// <summary>
    ///     关闭串口
    /// </summary>
    void Close();

    /// <summary>
    ///     发送数据
    /// </summary>
    /// <param name="data">要发送的数据</param>
    void SendData(byte[] data);

    /// <summary>
    ///     数据接收事件
    /// </summary>
    event EventHandler<byte[]> DataReceived;
}