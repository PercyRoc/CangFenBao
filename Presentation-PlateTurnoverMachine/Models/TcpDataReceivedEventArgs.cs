namespace Presentation_PlateTurnoverMachine.Models;

/// <summary>
/// TCP数据接收事件参数
/// </summary>
public class TcpDataReceivedEventArgs : EventArgs
{
    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="data">接收到的数据</param>
    /// <param name="receivedTime">接收时间</param>
    public TcpDataReceivedEventArgs(byte[] data, DateTime receivedTime)
    {
        Data = data;
        ReceivedTime = receivedTime;
    }

    /// <summary>
    /// 接收到的数据
    /// </summary>
    public byte[] Data { get; }
    
    /// <summary>
    /// 接收时间
    /// </summary>
    public DateTime ReceivedTime { get; }
}

/// <summary>
/// TCP模块数据接收事件参数
/// </summary>
public class TcpModuleDataReceivedEventArgs : TcpDataReceivedEventArgs
{
    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="config">TCP模块配置</param>
    /// <param name="data">接收到的数据</param>
    /// <param name="receivedTime">接收时间</param>
    public TcpModuleDataReceivedEventArgs(TcpConnectionConfig config, byte[] data, DateTime receivedTime)
        : base(data, receivedTime)
    {
        Config = config;
    }

    /// <summary>
    /// TCP模块配置
    /// </summary>
    public TcpConnectionConfig Config { get; }
} 