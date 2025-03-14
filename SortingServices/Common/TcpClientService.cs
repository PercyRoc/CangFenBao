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
        if (_isConnected) return;

        try
        {
            lock (_lockObject)
            {
                _client = new TcpClient();

                // 设置连接超时
                var result = _client.BeginConnect(_ipAddress, _port, null, null);
                if (!result.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    _client.Close();
                    throw new TimeoutException($"连接超时: {_deviceName}");
                }

                _client.EndConnect(result);

                // 验证连接是否真正建立
                if (!_client.Connected) throw new Exception("TCP连接未成功建立");

                // 获取网络流并验证是否可读写
                _stream = _client.GetStream();
                if (!_stream.CanRead || !_stream.CanWrite) throw new Exception("网络流不可读写");

                // 启动接收数据的线程
                _receiveThread = new Thread(ReceiveData)
                {
                    IsBackground = true,
                    Name = $"Receive-{_deviceName}"
                };
                _receiveThread.Start();

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
                StartReconnectThread();
            else
                throw;
        }
    }

    /// <summary>
    ///     启动重连线程
    /// </summary>
    private void StartReconnectThread()
    {
        if (_reconnectThread?.IsAlive == true) return; // 已有重连线程在运行

        _reconnectThread = new Thread(() =>
        {
            Log.Information("启动设备 {DeviceName} 的自动重连任务", _deviceName);

            var retryCount = 0;
            const int maxRetries = 5; // 最大重试次数

            while (!_isConnected && !_disposed && _autoReconnect && retryCount < maxRetries)
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
                    Thread.Sleep((int)delayMs);
                }

            if (retryCount >= maxRetries)
                Log.Error("设备 {DeviceName} 重连失败次数达到上限 {MaxRetries}，停止重连", _deviceName, maxRetries);
        })
        {
            IsBackground = true,
            Name = $"Reconnect-{_deviceName}"
        };

        _reconnectThread.Start();
    }

    /// <summary>
    ///     发送数据到设备
    /// </summary>
    public void Send(byte[] data)
    {
        if (!_isConnected || _stream == null) throw new InvalidOperationException("未连接到设备");

        try
        {
            lock (_lockObject)
            {
                _stream.Write(data, 0, data.Length);
                _stream.Flush();
            }
        }
        catch (Exception ex)
        {
            _isConnected = false;
            _connectionStatusCallback(false);
            Log.Error(ex, "向设备 {DeviceName} 发送数据失败", _deviceName);

            // 启动自动重连
            if (_autoReconnect && !_disposed) StartReconnectThread();

            throw;
        }
    }

    /// <summary>
    ///     接收数据的线程方法
    /// </summary>
    private void ReceiveData()
    {
        var buffer = new byte[1024];

        while (!_disposed && _isConnected && _stream != null)
            try
            {
                var bytesRead = _stream.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0)
                {
                    // 连接已关闭
                    _isConnected = false;
                    _connectionStatusCallback(false);

                    // 启动自动重连
                    if (_autoReconnect && !_disposed) StartReconnectThread();

                    break;
                }

                var data = new byte[bytesRead];
                Array.Copy(buffer, data, bytesRead);
                _dataReceivedCallback(data);
            }
            catch (Exception ex)
            {
                if (_disposed) break;

                _isConnected = false;
                _connectionStatusCallback(false);
                Log.Error(ex, "从设备 {DeviceName} 接收数据失败", _deviceName);

                // 启动自动重连
                if (_autoReconnect && !_disposed) StartReconnectThread();

                break;
            }
    }

    /// <summary>
    ///     断开与设备的连接
    /// </summary>
    private void Disconnect()
    {
        if (!_isConnected) return;

        lock (_lockObject)
        {
            _isConnected = false;
            _connectionStatusCallback(false);
            _stream?.Close();
            _client?.Close();
            Log.Information("已断开与设备 {DeviceName} 的连接", _deviceName);
        }
    }

    /// <summary>
    ///     获取连接状态
    /// </summary>
    public bool IsConnected()
    {
        return _isConnected;
    }

    /// <summary>
    ///     设置是否自动重连
    /// </summary>
    public void SetAutoReconnect(bool autoReconnect)
    {
        _autoReconnect = autoReconnect;
    }

    /// <summary>
    ///     释放资源
    /// </summary>
    private void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _autoReconnect = false; // 停止自动重连
            _cts.Cancel();
            Disconnect();

            // 等待接收线程结束
            if (_receiveThread?.IsAlive == true)
                try
                {
                    _receiveThread.Join(3000); // 等待最多3秒
                }
                catch (ThreadStateException)
                {
                    // 忽略线程状态异常
                }

            // 等待重连线程结束
            if (_reconnectThread?.IsAlive == true)
                try
                {
                    _reconnectThread.Join(3000); // 等待最多3秒
                }
                catch (ThreadStateException)
                {
                    // 忽略线程状态异常
                }

            _cts.Dispose();
            _stream?.Dispose();
            _client?.Dispose();
        }

        _disposed = true;
    }
}