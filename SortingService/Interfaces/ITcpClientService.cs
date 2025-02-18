namespace SortingService.Interfaces;

public interface ITcpClientService : IDisposable
{
    /// <summary>
    ///     IP地址
    /// </summary>
    string IpAddress { get; }

    /// <summary>
    ///     端口
    /// </summary>
    int Port { get; }

    /// <summary>
    ///     获取连接状态
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    ///     连接到服务器
    /// </summary>
    /// <param name="ipAddress">IP地址</param>
    /// <param name="port">端口</param>
    /// <param name="timeout">超时时间（毫秒）</param>
    Task ConnectAsync(string ipAddress, int port, int timeout = 5000);

    /// <summary>
    ///     断开连接
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    ///     发送数据
    /// </summary>
    /// <param name="data">要发送的数据</param>
    Task SendAsync(byte[] data);

    /// <summary>
    ///     接收数据
    /// </summary>
    /// <returns>接收到的数据</returns>
    Task<byte[]> ReceiveAsync();
}