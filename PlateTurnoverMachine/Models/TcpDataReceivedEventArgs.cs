namespace DongtaiFlippingBoardMachine.Models;

/// <summary>
///     TCP数据接收事件参数
/// </summary>
/// <remarks>
///     构造函数
/// </remarks>
/// <param name="data">接收到的数据</param>
/// <param name="receivedTime">接收时间</param>
public class TcpDataReceivedEventArgs(byte[]? data, DateTime receivedTime) : EventArgs
{

    /// <summary>
    ///     接收到的数据
    /// </summary>
    internal byte[]? Data { get; } = data;

    /// <summary>
    ///     接收时间
    /// </summary>
    internal DateTime ReceivedTime { get; } = receivedTime;
}

/// <summary>
///     TCP模块数据接收事件参数
/// </summary>
public class TcpModuleDataReceivedEventArgs : TcpDataReceivedEventArgs
{
    /// <summary>
    ///     构造函数
    /// </summary>
    /// <param name="config">TCP模块配置</param>
    /// <param name="data">接收到的数据</param>
    /// <param name="receivedTime">接收时间</param>
    internal TcpModuleDataReceivedEventArgs(TcpConnectionConfig config, byte[]? data, DateTime receivedTime)
        : base(data, receivedTime)
    {
        Config = config;
    }

    /// <summary>
    ///     TCP模块配置
    /// </summary>
    internal TcpConnectionConfig Config { get; }
}