using System.Net.Sockets;
using Serilog;

namespace SortingServices.Common;

/// <summary>
///     TCP客户端服务类，提供TCP连接、数据发送和接收功能
/// </summary>
public class TcpClientService : IDisposable
{
    private const int ReconnectInterval = 5000; // 重连间隔，单位毫秒
    private readonly Action<bool> _connectionStatusCallback;
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<byte[]> _dataReceivedCallback;
    private readonly string _deviceName;
    private readonly string _ipAddress;
    private readonly object _lockObject = new();
    private readonly int _port;
    private bool _autoReconnect;
    private TcpClient? _client;
    private bool _disposed;
    private bool _isConnected;
    private Thread? _receiveThread;
    private Thread? _reconnectThread;
    private NetworkStream? _stream;
    private readonly byte[] _receiveBuffer;  // 为每个客户端创建独立的接收缓冲区

    /// <summary>
    ///     创建TCP客户端服务
    /// </summary>
    /// <param name="deviceName">设备名称，用于日志记录</param>
    /// <param name="ipAddress">设备IP地址</param>
    /// <param name="port">设备端口</param>
    /// <param name="dataReceivedCallback">数据接收回调函数</param>
    /// <param name="connectionStatusCallback">连接状态变更回调函数</param>
    /// <param name="autoReconnect">是否自动重连</param>
    public TcpClientService(string deviceName, string ipAddress, int port, Action<byte[]> dataReceivedCallback,
        Action<bool> connectionStatusCallback, bool autoReconnect = true)
    {
        _deviceName = deviceName;
        _ipAddress = ipAddress;
        _port = port;
        _dataReceivedCallback = dataReceivedCallback;
        _connectionStatusCallback = connectionStatusCallback;
        _autoReconnect = autoReconnect;
        _receiveBuffer = new byte[1024];  // 初始化接收缓冲区
        Log.Debug("设备 {DeviceName} 创建TCP客户端服务，IP: {IpAddress}, 端口: {Port}", _deviceName, _ipAddress, _port);
    }

    /// <summary>
    ///     释放资源
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     连接到设备
    /// </summary>
    public void Connect(int timeoutMs = 5000)
    {
        if (_isConnected)
        {
            Log.Debug("设备 {DeviceName} 已经连接，跳过连接操作", _deviceName);
            return;
        }

        // 检查IP地址和端口是否为空或为0
        if (string.IsNullOrEmpty(_ipAddress) || _port == 0)
        {
            Log.Warning("设备 {DeviceName} 的IP地址或端口未配置，不启动连接", _deviceName);
            return;
        }

        try
        {
            lock (_lockObject)
            {
                Log.Debug("设备 {DeviceName} 开始建立连接...", _deviceName);
                _client = new TcpClient();

                // 设置连接超时
                var result = _client.BeginConnect(_ipAddress, _port, null, null);
                Log.Debug("设备 {DeviceName} 等待连接完成，超时时间: {Timeout}ms", _deviceName, timeoutMs);
                
                if (!result.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    _client.Close();
                    Log.Error("设备 {DeviceName} 连接超时", _deviceName);
                    throw new TimeoutException($"连接超时: {_deviceName}");
                }

                _client.EndConnect(result);
                Log.Debug("设备 {DeviceName} TCP连接已建立", _deviceName);

                // 验证连接是否真正建立
                if (!_client.Connected)
                {
                    Log.Error("设备 {DeviceName} TCP连接未成功建立", _deviceName);
                    throw new Exception("TCP连接未成功建立");
                }

                // 获取网络流并验证是否可读写
                _stream = _client.GetStream();
                if (!_stream.CanRead || !_stream.CanWrite)
                {
                    Log.Error("设备 {DeviceName} 网络流不可读写", _deviceName);
                    throw new Exception("网络流不可读写");
                }

                // 启动接收数据的线程
                _receiveThread = new Thread(ReceiveData)
                {
                    IsBackground = true,
                    Name = $"Receive-{_deviceName}"
                };
                _receiveThread.Start();
                Log.Debug("设备 {DeviceName} 接收线程已启动", _deviceName);

                // 确保所有初始化完成后再设置连接状态和触发回调
                _isConnected = true;
                _connectionStatusCallback(true);

                Log.Information("已连接到设备 {DeviceName} ({IpAddress}:{Port})", _deviceName, _ipAddress, _port);
            }
        }
        catch (Exception ex)
        {
            _isConnected = false;
            _connectionStatusCallback(false);
            Log.Error(ex, "连接设备 {DeviceName} ({IpAddress}:{Port}) 失败", _deviceName, _ipAddress, _port);

            // 清理资源
            lock (_lockObject)
            {
                _stream?.Dispose();
                _client?.Dispose();
                _stream = null;
                _client = null;
            }

            // 如果不是初始连接，则启动自动重连
            if (_autoReconnect && !_disposed)
            {
                Log.Debug("设备 {DeviceName} 启动自动重连", _deviceName);
                StartReconnectThread();
            }
            else
                throw;
        }
    }

    /// <summary>
    ///     启动重连线程
    /// </summary>
    private void StartReconnectThread()
    {
        if (_reconnectThread?.IsAlive == true)
        {
            Log.Debug("设备 {DeviceName} 已有重连线程在运行，跳过", _deviceName);
            return;
        }

        _reconnectThread = new Thread(() =>
        {
            Log.Information("启动设备 {DeviceName} 的自动重连任务", _deviceName);

            var retryCount = 0;
            const int maxRetries = 5; // 最大重试次数

            while (!_isConnected && !_disposed && _autoReconnect && retryCount < maxRetries)
            {
                try
                {
                    Log.Information("尝试重新连接设备 {DeviceName} ({IpAddress}:{Port}), 第 {RetryCount} 次尝试",
                        _deviceName, _ipAddress, _port, retryCount + 1);

                    lock (_lockObject)
                    {
                        _client?.Dispose();
                        _stream?.Dispose();
                        _client = null;
                        _stream = null;
                    }

                    Connect(); // 5秒连接超时
                    return; // 连接成功，退出重连循环
                }
                catch (Exception ex)
                {
                    retryCount++;
                    Log.Error(ex, "重新连接设备 {DeviceName} ({IpAddress}:{Port}) 失败", _deviceName, _ipAddress, _port);

                    // 使用指数退避算法计算等待时间
                    var delayMs = Math.Min(ReconnectInterval * Math.Pow(2, retryCount), 30000); // 最大等待30秒
                    Log.Debug("设备 {DeviceName} 等待 {Delay}ms 后重试", _deviceName, delayMs);
                    Thread.Sleep((int)delayMs);
                }
            }

            if (retryCount >= maxRetries)
                Log.Error("设备 {DeviceName} 重连失败次数达到上限 {MaxRetries}，停止重连", _deviceName, maxRetries);
        })
        {
            IsBackground = true,
            Name = $"Reconnect-{_deviceName}"
        };

        _reconnectThread.Start();
        Log.Debug("设备 {DeviceName} 重连线程已启动", _deviceName);
    }

    /// <summary>
    ///     发送数据到设备
    /// </summary>
    internal void Send(byte[] data)
    {
        if (!_isConnected || _stream == null)
        {
            Log.Error("设备 {DeviceName} 未连接，无法发送数据", _deviceName);
            throw new InvalidOperationException("未连接到设备");
        }

        try
        {
            lock (_lockObject)
            {
                Log.Debug("设备 {DeviceName} 发送数据: {Data}", _deviceName, BitConverter.ToString(data));
                _stream.Write(data, 0, data.Length);
                _stream.Flush();
                Log.Debug("设备 {DeviceName} 数据发送完成", _deviceName);
            }
        }
        catch (Exception ex)
        {
            _isConnected = false;
            _connectionStatusCallback(false);
            Log.Error(ex, "向设备 {DeviceName} 发送数据失败", _deviceName);

            // 启动自动重连
            if (_autoReconnect && !_disposed)
            {
                Log.Debug("设备 {DeviceName} 发送失败后启动自动重连", _deviceName);
                StartReconnectThread();
            }

            throw;
        }
    }

    /// <summary>
    ///     接收数据的线程方法
    /// </summary>
    private void ReceiveData()
    {
        Log.Debug("设备 {DeviceName} 的接收线程开始运行", _deviceName);

        // 使用局部变量确保在循环中引用的是同一个流和客户端对象

        while (!_disposed)
        {
            try
            {
                // 在循环开始时获取当前的流和客户端引用
                // 使用 lock 确保在检查和获取时 client 和 stream 状态一致
                TcpClient? currentClient;
                NetworkStream? currentStream;
                lock (_lockObject)
                {
                    currentStream = _stream;
                    currentClient = _client;
                }

                // 如果在 dispose 过程中或断开连接后 stream/client 被置为 null，则等待
                if (currentClient == null || currentStream == null || !currentClient.Connected) // 保留 Connected 检查，作为快速失败路径，但主要依赖 Read 操作
                {
                    if (!_disposed && _autoReconnect) // 只有在允许自动重连且未 Dispose 时才认为需要重连
                    {
                         Log.Warning("设备 {DeviceName} 连接似乎已断开或未就绪，等待重连或状态更新...", _deviceName);
                        _isConnected = false; // 标记为断开
                        _connectionStatusCallback(false);
                    } else if (!_disposed) {
                         Log.Debug("设备 {DeviceName} 连接已断开或未就绪，且不自动重连，接收线程暂停。", _deviceName);
                    }
                    Thread.Sleep(1000); // 等待1秒后重试检查状态
                    continue;
                }

                // 检查流是否可读，如果不可读，可能是暂时状态或连接问题
                if (!currentStream.CanRead)
                {
                    Log.Warning("设备 {DeviceName} 网络流不可读，短暂等待后重试...", _deviceName);
                    Thread.Sleep(500); // 短暂等待后继续循环检查
                    continue;
                }
                // 设置读取超时 (可以考虑将其设为可配置项)
                // 注意: ReadTimeout 需要在每次循环中设置可能不是必需的，取决于 NetworkStream 的实现，
                // 但为确保设置生效，放在这里是安全的。如果性能敏感，可以移到连接建立后设置一次。
                if (currentStream.ReadTimeout != 1000) // 避免重复设置
                {
                   currentStream.ReadTimeout = 1000; // 1秒超时
                }

                int bytesRead;
                try
                {
                    // *** 核心读操作 ***
                    bytesRead = currentStream.Read(_receiveBuffer, 0, _receiveBuffer.Length);
                }
                catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut })
                {
                    // 读取超时是预期行为，表示在超时时间内没有数据到达，继续循环等待即可
                    Log.Verbose("设备 {DeviceName} 读取数据超时，继续等待...", _deviceName); // 使用 Verbose 级别，因为这很常见
                    continue; // 继续下一次循环等待
                }
                catch (IOException ex)
                {
                    // 其他类型的 IO 异常，通常表示连接层面的问题
                    var socketEx = ex.InnerException as SocketException;
                    Log.Error(ex, "设备 {DeviceName} 读取数据时发生网络IO错误。SocketErrorCode: {SocketErrorCode}. 标记连接为断开，等待重连...",
                        _deviceName, socketEx?.SocketErrorCode); // 记录具体的 SocketErrorCode
                    _isConnected = false;
                    _connectionStatusCallback(false);
                    // 不需要在这里启动重连，依赖外部重连逻辑或下一次循环的检查
                    Thread.Sleep(1000); // 发生错误后等待一会再重试，避免CPU空转
                    continue;
                }
                // 不再捕捉 ObjectDisposedException，因为理论上 _disposed 标志会先阻止循环，
                // 或者 stream/client 为 null 的检查会先生效。如果仍然发生，让它抛到外层catch处理。


                // 检查读取到的字节数
                if (bytesRead == 0)
                {
                    // 根据用户反馈，读取到 0 字节不一定代表连接关闭。
                    // 记录此事件，但继续尝试读取，依赖后续操作失败来判断连接状态。
                    Log.Debug("设备 {DeviceName} 读取到 0 字节。继续监听...", _deviceName);
                    continue; // 继续循环，尝试下一次读取
                }

                // 成功读取到数据
                Log.Debug("设备 {DeviceName} 接收到 {BytesRead} 字节数据: {Data}",
                    _deviceName, bytesRead, BitConverter.ToString(_receiveBuffer, 0, bytesRead));

                // 创建新的数据数组，避免数据混淆
                var data = new byte[bytesRead];
                Array.Copy(_receiveBuffer, data, bytesRead);

                // 使用 Task.Run 异步处理数据，避免阻塞接收线程
                _ = Task.Run(() =>
                {
                    try
                    {
                        _dataReceivedCallback(data);
                    }
                    catch (Exception callbackEx) // 捕获回调函数中的异常
                    {
                        Log.Error(callbackEx, "设备 {DeviceName} 处理接收到的数据时发生未处理的异常", _deviceName);
                    }
                });
            }
            // 将 ObjectDisposedException 移到这里处理，因为它可能在访问 stream 或 client 时发生
            catch (ObjectDisposedException objEx)
            {
                 if (_disposed)
                 {
                    Log.Debug("设备 {DeviceName} 对象已按预期释放，接收线程准备退出。", _deviceName);
                    break; // 如果是 Dispose 导致的，直接退出循环
                 }
                 Log.Warning(objEx, "设备 {DeviceName} 对象已被释放，可能在断开连接过程中。等待重连...", _deviceName);
                 _isConnected = false;
                 _connectionStatusCallback(false);
                 Thread.Sleep(1000);
            }
            catch (Exception ex) // 捕获其他所有未预料到的异常
            {
                if (_disposed)
                {
                     Log.Debug("设备 {DeviceName} 在 Dispose 过程中发生异常，接收线程准备退出。异常: {Exception}", _deviceName, ex.ToString());
                     break; // 如果是 Dispose 过程中的异常，退出
                }

                // 记录完整的异常信息
                Log.Error(ex, "设备 {DeviceName} 在接收数据循环中发生未处理的严重异常。标记连接为断开，等待重连...", _deviceName);
                _isConnected = false;
                _connectionStatusCallback(false);
                 // 关闭可能处于未知状态的资源
                lock (_lockObject)
                {
                    try { _stream?.Close(); } catch (Exception streamEx) { Log.Warning(streamEx, "设备 {DeviceName} 在异常处理中关闭流失败", _deviceName); }
                    try { _client?.Close(); } catch (Exception clientEx) { Log.Warning(clientEx, "设备 {DeviceName} 在异常处理中关闭客户端失败", _deviceName); }
                    _stream = null;
                    _client = null;
                }
                Thread.Sleep(1000); // 等待1秒后重试
            }
        }

        Log.Information("设备 {DeviceName} 的接收线程已停止运行", _deviceName);
    }

    /// <summary>
    ///     断开与设备的连接
    /// </summary>
    private void Disconnect()
    {
        TcpClient? clientToClose;
        NetworkStream? streamToClose;

        lock (_lockObject)
        {
            // 检查是否真的需要断开
             if (!_isConnected && _client == null && _stream == null)
            {
                Log.Debug("设备 {DeviceName} 已经处于断开状态，无需重复断开", _deviceName);
                return;
            }


            Log.Debug("设备 {DeviceName} 开始断开连接...", _deviceName);
            _isConnected = false; // 标记为断开

             // 先将成员变量置为 null，防止其他线程（如 ReceiveData）在 Close() 执行期间访问它们
            clientToClose = _client;
            streamToClose = _stream;
            _client = null;
            _stream = null;
        }
         // 在 lock 外部执行可能阻塞的操作
        try
        {
             streamToClose?.Close();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "设备 {DeviceName} 关闭 NetworkStream 时发生异常", _deviceName);
        }
        try
        {
            clientToClose?.Close();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "设备 {DeviceName} 关闭 TcpClient 时发生异常", _deviceName);
        }


        // 回调现在可以在这里安全触发，因为它发生在实际关闭操作之后
        _connectionStatusCallback(false);
        Log.Information("已断开与设备 {DeviceName} 的连接", _deviceName);

    }

    /// <summary>
    ///     获取连接状态
    /// </summary>
    internal bool IsConnected()
    {
        return _isConnected;
    }

    /// <summary>
    ///     释放资源
    /// </summary>
    private void Dispose(bool disposing)
    {
        if (_disposed)
        {
            Log.Debug("设备 {DeviceName} 已经释放，无需重复释放", _deviceName);
            return;
        }

        if (disposing)
        {
            Log.Debug("设备 {DeviceName} 开始释放资源...", _deviceName);
            _autoReconnect = false; // 停止自动重连
            _cts.Cancel();
            Disconnect();

            // 等待接收线程结束
            if (_receiveThread?.IsAlive == true)
            {
                Log.Debug("设备 {DeviceName} 等待接收线程结束...", _deviceName);
                try
                {
                    _receiveThread.Join(3000); // 等待最多3秒
                }
                catch (ThreadStateException)
                {
                    Log.Debug("设备 {DeviceName} 接收线程状态异常", _deviceName);
                }
            }

            // 等待重连线程结束
            if (_reconnectThread?.IsAlive == true)
            {
                Log.Debug("设备 {DeviceName} 等待重连线程结束...", _deviceName);
                try
                {
                    _reconnectThread.Join(3000); // 等待最多3秒
                }
                catch (ThreadStateException)
                {
                    Log.Debug("设备 {DeviceName} 重连线程状态异常", _deviceName);
                }
            }

            _cts.Dispose();
            _stream?.Dispose();
            _client?.Dispose();
            Log.Debug("设备 {DeviceName} 资源释放完成", _deviceName);
        }

        _disposed = true;
    }
}