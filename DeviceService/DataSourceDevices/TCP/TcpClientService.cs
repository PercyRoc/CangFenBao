using System.Net.Sockets;
using Serilog;

namespace DeviceService.DataSourceDevices.TCP;

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
        // 先检查是否已释放
        if (_disposed)
        {
            Log.Warning("设备 {DeviceName} 服务已被释放，无法连接", _deviceName);
            return;
        }

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
                // 锁内再次检查是否已释放
                if (_disposed) return;

                Log.Debug("设备 {DeviceName} 开始建立连接...", _deviceName);
                _client = new TcpClient();

                // 设置连接超时
                var result = _client.BeginConnect(_ipAddress, _port, null, null);
                Log.Debug("设备 {DeviceName} 等待连接完成，超时时间: {Timeout}ms", _deviceName, timeoutMs);
                
                // 添加对取消的支持
                var waitResult = result.AsyncWaitHandle.WaitOne(timeoutMs);
                
                // 检查是否已取消
                if (_cts.IsCancellationRequested)
                {
                    _client.Close();
                    Log.Information("设备 {DeviceName} 连接操作被取消", _deviceName);
                    throw new OperationCanceledException(_cts.Token);
                }
                
                if (!waitResult)
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
                if (_receiveThread == null || !_receiveThread.IsAlive)
                {
                    _receiveThread = new Thread(ReceiveData)
                    {
                        IsBackground = true,
                        Name = $"Receive-{_deviceName}"
                    };
                    _receiveThread.Start();
                    Log.Debug("设备 {DeviceName} 接收线程已启动", _deviceName);
                }

                // 确保所有初始化完成后再设置连接状态和触发回调
                _isConnected = true;
                _connectionStatusCallback(true);

                Log.Information("已连接到设备 {DeviceName} ({IpAddress}:{Port})", _deviceName, _ipAddress, _port);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("设备 {DeviceName} 连接操作被取消", _deviceName);
            Disconnect(); // 使用 Disconnect 处理清理
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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
            if (_autoReconnect && !_disposed && !_cts.IsCancellationRequested)
            {
                Log.Debug("设备 {DeviceName} 启动自动重连", _deviceName);
                StartReconnectThread();
            }
        }
    }

    /// <summary>
    ///     启动重连线程
    /// </summary>
    private void StartReconnectThread()
    {
        // 检查是否已释放
        if (_disposed || _cts.IsCancellationRequested) return;

        lock (_lockObject)
        {
            if (_reconnectThread?.IsAlive == true)
            {
                Log.Debug("设备 {DeviceName} 已有重连线程在运行，跳过", _deviceName);
                return;
            }

            // 锁内再次检查是否已释放
            if (_disposed || _cts.IsCancellationRequested) return;

            _reconnectThread = new Thread(() =>
            {
                Log.Information("启动设备 {DeviceName} 的自动重连任务", _deviceName);

                var retryCount = 0;
                const int maxRetries = 5; // 最大重试次数

                // 添加对取消令牌的检查
                while (!_isConnected && !_disposed && !_cts.IsCancellationRequested && _autoReconnect && retryCount < maxRetries)
                {
                    try
                    {
                        // 重连前检查取消
                        if (_cts.IsCancellationRequested) break;

                        Log.Information("尝试重新连接设备 {DeviceName} ({IpAddress}:{Port}), 第 {RetryCount} 次尝试",
                            _deviceName, _ipAddress, _port, retryCount + 1);

                        Connect(); // 连接成功后会退出循环
                        return; // 连接成功，退出重连循环
                    }
                    catch (OperationCanceledException)
                    {
                        Log.Information("设备 {DeviceName} 重连尝试被取消", _deviceName);
                        break; // 取消时退出循环
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        retryCount++;
                        Log.Error(ex, "重新连接设备 {DeviceName} ({IpAddress}:{Port}) 失败", _deviceName, _ipAddress, _port);

                        // 等待前检查取消
                        if (_cts.IsCancellationRequested) break;

                        // 使用指数退避算法计算等待时间
                        var delayMs = Math.Min(ReconnectInterval * Math.Pow(2, retryCount), 30000); // 最大等待30秒
                        Log.Debug("设备 {DeviceName} 等待 {Delay}ms 后重试", _deviceName, delayMs);
                        
                        // 使用可取消的等待
                        if (_cts.Token.WaitHandle.WaitOne((int)delayMs))
                        {
                            // WaitOne 返回 true 表示令牌被取消
                            Log.Debug("设备 {DeviceName} 重连等待期间被取消", _deviceName);
                            break;
                        }
                    }
                }

                if (retryCount >= maxRetries && !_cts.IsCancellationRequested && !_disposed)
                    Log.Error("设备 {DeviceName} 重连失败次数达到上限 {MaxRetries}，停止重连", _deviceName, maxRetries);
                else if (_cts.IsCancellationRequested)
                    Log.Information("设备 {DeviceName} 重连任务因取消请求而停止", _deviceName);
                else if (_disposed)
                    Log.Information("设备 {DeviceName} 重连任务因服务已释放而停止", _deviceName);
                else if (_isConnected)
                    Log.Information("设备 {DeviceName} 重连成功，重连任务结束", _deviceName);
            })
            {
                IsBackground = true,
                Name = $"Reconnect-{_deviceName}"
            };

            _reconnectThread.Start();
            Log.Debug("设备 {DeviceName} 重连线程已启动", _deviceName);
        }
    }

    /// <summary>
    ///     发送数据到设备
    /// </summary>
    public void Send(byte[] data)
    {
        // 检查是否已释放
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TcpClientService), $"设备 {_deviceName} 服务已释放，无法发送数据");
        }

        if (data.Length == 0)
        {
            Log.Warning("设备 {DeviceName} 尝试发送空数据", _deviceName);
            return;
        }

        lock (_lockObject)
        {
            // 锁内再次检查是否已释放或未连接
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(TcpClientService), $"设备 {_deviceName} 服务已释放，无法发送数据");
            }

            if (!_isConnected || _stream == null || _client == null)
            {
                Log.Error("设备 {DeviceName} 未连接，无法发送数据", _deviceName);
                throw new InvalidOperationException("未连接到设备");
            }

            try
            {
                Log.Debug("设备 {DeviceName} 发送数据: {Data}", _deviceName, BitConverter.ToString(data.Length > 50 ? data.Take(50).ToArray() : data));
                _stream.Write(data, 0, data.Length);
                _stream.Flush();
                Log.Debug("设备 {DeviceName} 数据发送完成", _deviceName);
            }
            catch (Exception ex)
            {
                _isConnected = false;
                _connectionStatusCallback(false);
                Log.Error(ex, "向设备 {DeviceName} 发送数据失败", _deviceName);

                // 启动自动重连
                if (_autoReconnect && !_disposed && !_cts.IsCancellationRequested)
                {
                    Log.Debug("设备 {DeviceName} 发送失败后启动自动重连", _deviceName);
                    StartReconnectThread();
                }
                throw;
            }
        }
    }

    /// <summary>
    ///     接收数据的线程方法
    /// </summary>
    private void ReceiveData()
    {
        Log.Debug("设备 {DeviceName} 的接收线程开始运行", _deviceName);
        
        // 改进循环条件，包含对取消令牌的检查
        while (!_disposed && !_cts.IsCancellationRequested)
        {
            try
            {
                // 在循环开始时获取当前的流和客户端引用
                // 使用 lock 确保在检查和获取时 client 和 stream 状态一致
                TcpClient? currentClient;
                NetworkStream? currentStream;
                
                lock (_lockObject)
                {
                    // 锁内再次检查是否已释放或取消
                    if (_disposed || _cts.IsCancellationRequested) break;
                    
                    currentStream = _stream;
                    currentClient = _client;
                }

                // 如果在 dispose 过程中或断开连接后 stream/client 被置为 null，则等待
                if (currentClient == null || currentStream == null || !currentClient.Connected)
                {
                    string reason = "未知原因"; // 默认原因
                    if (currentClient == null)
                    {
                        reason = "TcpClient 实例为 null";
                    }
                    else if (currentStream == null)
                    {
                        reason = "NetworkStream 实例为 null";
                    }
                    else if (!currentClient.Connected)
                    {
                        // Socket 属性可能会抛出 ObjectDisposedException，需要处理
                        try
                        {
                           reason = $"TcpClient.Connected 为 false (Socket Connected: {currentClient.Client.Connected})"; // 添加底层 Socket 连接状态
                        }
                        catch (ObjectDisposedException)
                        {
                            reason = "TcpClient.Connected 为 false (底层 Socket 已释放)";
                        }
                        catch (Exception ex) // 捕获其他可能的异常
                        {
                             reason = $"TcpClient.Connected 为 false (检查底层 Socket 状态时出错: {ex.GetType().Name})";
                             Log.Warning(ex, "设备 {DeviceName} 检查底层 Socket 连接状态时发生异常", _deviceName);
                        }
                    }
                    Log.Warning("设备 {DeviceName} 连接似乎已断开或未就绪 ({Reason})，等待重连或状态更新...", _deviceName, reason);

                    // 只有在原本是连接状态时才触发回调
                    if (_isConnected)
                    {
                        _isConnected = false;
                        _connectionStatusCallback(false);
                        
                        // 启动重连（如果允许）
                        if (_autoReconnect && !_disposed && !_cts.IsCancellationRequested)
                        {
                            StartReconnectThread();
                        }
                    }

                    // 使用可取消的等待
                    if (_cts.Token.WaitHandle.WaitOne(1000))
                    {
                        // 等待被取消，退出循环
                        break;
                    }
                    continue;
                }

                // 检查流是否可读，如果不可读，可能是暂时状态或连接问题
                if (!currentStream.CanRead)
                {
                    Log.Warning("设备 {DeviceName} 网络流不可读，短暂等待后重试...", _deviceName);
                    
                    // 使用可取消的等待
                    if (_cts.Token.WaitHandle.WaitOne(500))
                    {
                        // 等待被取消，退出循环
                        break;
                    }
                    continue;
                }
                
                // 设置读取超时
                try
                {
                    if (currentStream.ReadTimeout != 1000) // 避免重复设置
                    {
                       currentStream.ReadTimeout = 1000; // 1秒超时
                    }
                }
                catch (Exception ex) when (ex is ObjectDisposedException || ex is InvalidOperationException)
                {
                    Log.Warning(ex, "设备 {DeviceName} 设置读取超时时发生异常", _deviceName);
                    
                    // 使用可取消的等待
                    if (_cts.Token.WaitHandle.WaitOne(500))
                    {
                        // 等待被取消，退出循环
                        break;
                    }
                    continue;
                }

                // 读取前检查取消令牌
                if (_cts.IsCancellationRequested) break;

                int bytesRead;
                try
                {
                    // 核心读操作
                    bytesRead = currentStream.Read(_receiveBuffer, 0, _receiveBuffer.Length);
                }
                catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut })
                {
                    // 读取超时是预期行为，表示在超时时间内没有数据到达，继续循环等待即可
                    Log.Verbose("设备 {DeviceName} 读取数据超时，继续等待...", _deviceName); // 使用 Verbose 级别，因为这很常见
                    // 在进入下一个循环前检查取消
                    if (_cts.IsCancellationRequested) break;
                    continue; // 继续下一次循环等待
                }
                catch (IOException ex)
                {
                    // 读取操作后检查取消令牌
                    if (_cts.IsCancellationRequested) break;
                    
                    // 其他类型的 IO 异常，通常表示连接层面的问题
                    var socketEx = ex.InnerException as SocketException;
                    Log.Error(ex, "设备 {DeviceName} 读取数据时发生网络IO错误。SocketErrorCode: {SocketErrorCode}. 标记连接为断开，等待重连...",
                        _deviceName, socketEx?.SocketErrorCode); // 记录具体的 SocketErrorCode
                    
                    if (_isConnected)
                    {
                        _isConnected = false;
                        _connectionStatusCallback(false);
                        
                        // 启动重连（如果允许）
                        if (_autoReconnect && !_disposed && !_cts.IsCancellationRequested)
                        {
                            StartReconnectThread();
                        }
                    }
                    
                    // 使用可取消的等待
                    if (_cts.Token.WaitHandle.WaitOne(1000))
                    {
                        // 等待被取消，退出循环
                        break;
                    }
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    // 如果是因为释放导致的，则退出循环
                    if (_disposed || _cts.IsCancellationRequested) break;
                    
                    Log.Warning("设备 {DeviceName} 读取时流已被释放，可能在断开连接过程中。等待重连...", _deviceName);
                    
                    if (_isConnected)
                    {
                        _isConnected = false;
                        _connectionStatusCallback(false);
                        
                        // 启动重连（如果允许）
                        if (_autoReconnect && !_disposed && !_cts.IsCancellationRequested)
                        {
                            StartReconnectThread();
                        }
                    }
                    
                    // 使用可取消的等待
                    if (_cts.Token.WaitHandle.WaitOne(1000))
                    {
                        // 等待被取消，退出循环
                        break;
                    }
                    continue;
                }

                // 读取操作后检查取消令牌
                if (_cts.IsCancellationRequested) break;

                // 检查读取到的字节数
                if (bytesRead == 0)
                {
                    // 读取到 0 字节不一定代表连接关闭
                    Log.Debug("设备 {DeviceName} 读取到 0 字节。继续监听...", _deviceName);
                    continue; // 继续循环，尝试下一次读取
                }

                // 读取操作后检查取消令牌
                if (_cts.IsCancellationRequested) break;

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
            catch (OperationCanceledException)
            {
                Log.Information("设备 {DeviceName} 接收线程被取消", _deviceName);
                break; // 退出循环
            }
            catch (Exception ex) // 捕获其他所有未预料到的异常
            {
                // 检查是否已释放或取消
                if (_disposed || _cts.IsCancellationRequested) break;

                Log.Error(ex, "设备 {DeviceName} 在接收数据循环中发生未处理的严重异常。标记连接为断开，等待重连...", _deviceName);
                
                if (_isConnected)
                {
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
                    
                    // 启动重连（如果允许）
                    if (_autoReconnect && !_disposed && !_cts.IsCancellationRequested)
                    {
                        StartReconnectThread();
                    }
                }
                
                // 使用可取消的等待
                if (_cts.Token.WaitHandle.WaitOne(1000))
                {
                    // 等待被取消，退出循环
                    break;
                }
            }
        }

        Log.Information("设备 {DeviceName} 的接收线程已停止运行 (Disposed={IsDisposed}, Cancelled={IsCancelled})",
            _deviceName, _disposed, _cts.IsCancellationRequested);
    }

    /// <summary>
    ///     断开与设备的连接
    /// </summary>
    private void Disconnect()
    {
        TcpClient? clientToClose;
        NetworkStream? streamToClose;
        var needsCallback = false;

        lock (_lockObject)
        {
            // 检查是否已经处于断开状态或已释放
            if ((!_isConnected && _client == null && _stream == null) || _disposed)
            {
                if (!_disposed) Log.Debug("设备 {DeviceName} 已经处于断开状态或已释放，无需重复断开", _deviceName);
                return;
            }

            Log.Debug("设备 {DeviceName} 开始断开连接...", _deviceName);

            // 在更改状态前记录是否需要回调
            if (_isConnected)
            {
                needsCallback = true;
            }
            _isConnected = false; // 标记为断开

            // 保存当前的引用，并将成员变量置为 null
            clientToClose = _client;
            streamToClose = _stream;
            _client = null;
            _stream = null;
        }

        // 在锁外部执行可能阻塞的操作
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
        if (needsCallback && !_disposed)
        {
            _connectionStatusCallback(false);
        }
        
        Log.Information("已断开与设备 {DeviceName} 的连接", _deviceName);
    }

    /// <summary>
    ///     获取连接状态
    /// </summary>
    public bool IsConnected()
    {
        return _isConnected && !_disposed;
    }

    /// <summary>
    ///     释放资源
    /// </summary>
    private void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            Log.Debug("设备 {DeviceName} 开始释放资源...", _deviceName);
            
            // 首先禁止自动重连，防止后续可能的重连尝试
            _autoReconnect = false;
            
            // 首先取消所有操作，这样后台线程会收到取消信号
            try
            {
                _cts.Cancel();
                Log.Debug("设备 {DeviceName} 已取消所有操作", _deviceName);
            }
            catch (ObjectDisposedException)
            {
                // 忽略如果已经释放
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "设备 {DeviceName} 取消操作时发生异常", _deviceName);
            }
            
            // 断开连接（关闭流和客户端）
            Disconnect();

            // 等待接收线程结束
            Thread? currentReceiveThread = _receiveThread;
            if (currentReceiveThread?.IsAlive == true)
            {
                Log.Debug("设备 {DeviceName} 等待接收线程结束...", _deviceName);
                try
                {
                    if (!currentReceiveThread.Join(3000)) // 等待最多3秒
                    {
                        Log.Warning("设备 {DeviceName} 等待接收线程结束超时", _deviceName);
                    }
                }
                catch (ThreadStateException ex)
                {
                    Log.Warning(ex, "设备 {DeviceName} 等待接收线程时发生异常", _deviceName);
                }
            }

            // 等待重连线程结束
            Thread? currentReconnectThread = _reconnectThread;
            if (currentReconnectThread?.IsAlive == true)
            {
                Log.Debug("设备 {DeviceName} 等待重连线程结束...", _deviceName);
                try
                {
                    if (!currentReconnectThread.Join(3000)) // 等待最多3秒
                    {
                        Log.Warning("设备 {DeviceName} 等待重连线程结束超时", _deviceName);
                    }
                }
                catch (ThreadStateException ex)
                {
                    Log.Warning(ex, "设备 {DeviceName} 等待重连线程时发生异常", _deviceName);
                }
            }

            // 最后释放 CancellationTokenSource
            try
            {
                _cts.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "设备 {DeviceName} 释放 CancellationTokenSource 时发生异常", _deviceName);
            }

            Log.Debug("设备 {DeviceName} 资源释放完成", _deviceName);
        }

        _disposed = true;
    }
}