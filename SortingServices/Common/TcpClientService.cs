using System.Net.Sockets;
using Serilog;

namespace SortingServices.Common;

/// <summary>
/// TCP客户端服务类，提供TCP连接、数据发送和接收功能
/// </summary>
public class TcpClientService : IDisposable
{
    private readonly string _deviceName;
    private readonly string _ipAddress;
    private readonly int _port;
    private readonly Action<byte[]> _dataReceivedCallback;
    private readonly Action<bool> _connectionStatusCallback;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private bool _isConnected;
    private bool _disposed;
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveTask;
    private Task? _reconnectTask;
    private bool _autoReconnect;
    private const int ReconnectInterval = 5000; // 重连间隔，单位毫秒

    /// <summary>
    /// 创建TCP客户端服务
    /// </summary>
    /// <param name="deviceName">设备名称，用于日志记录</param>
    /// <param name="ipAddress">设备IP地址</param>
    /// <param name="port">设备端口</param>
    /// <param name="dataReceivedCallback">数据接收回调函数</param>
    /// <param name="connectionStatusCallback">连接状态变更回调函数</param>
    /// <param name="autoReconnect">是否自动重连</param>
    public TcpClientService(string deviceName, string ipAddress, int port, Action<byte[]> dataReceivedCallback, Action<bool> connectionStatusCallback, bool autoReconnect = true)
    {
        _deviceName = deviceName;
        _ipAddress = ipAddress;
        _port = port;
        _dataReceivedCallback = dataReceivedCallback;
        _connectionStatusCallback = connectionStatusCallback;
        _autoReconnect = autoReconnect;
    }

    /// <summary>
    /// 连接到设备
    /// </summary>
    public async Task ConnectAsync()
    {
        if (_isConnected) return;

        try
        {
            _client = new TcpClient();
            await _client.ConnectAsync(_ipAddress, _port);
            _stream = _client.GetStream();
            _isConnected = true;
            _connectionStatusCallback(true);

            // 启动接收数据的任务
            _receiveTask = Task.Run(ReceiveDataAsync, _cts.Token);

            Log.Information("已连接到设备 {DeviceName} ({IpAddress}:{Port})", _deviceName, _ipAddress, _port);
        }
        catch (Exception ex)
        {
            _isConnected = false;
            _connectionStatusCallback(false);
            Log.Error(ex, "连接设备 {DeviceName} ({IpAddress}:{Port}) 失败", _deviceName, _ipAddress, _port);
            
            // 启动自动重连
            if (_autoReconnect && !_disposed)
            {
                StartReconnectTask();
            }
            else
            {
                throw;
            }
        }
    }

    /// <summary>
    /// 启动重连任务
    /// </summary>
    private void StartReconnectTask()
    {
        if (_reconnectTask is { IsCompleted: false })
        {
            return; // 已有重连任务在运行
        }

        _reconnectTask = Task.Run(async () =>
        {
            Log.Information("启动设备 {DeviceName} 的自动重连任务", _deviceName);
            
            while (!_isConnected && !_disposed && _autoReconnect)
            {
                try
                {
                    Log.Information("尝试重新连接设备 {DeviceName} ({IpAddress}:{Port})", _deviceName, _ipAddress, _port);
                    
                    _client?.Dispose();
                    _stream?.Dispose();
                    
                    _client = new TcpClient();
                    await _client.ConnectAsync(_ipAddress, _port);
                    _stream = _client.GetStream();
                    _isConnected = true;
                    _connectionStatusCallback(true);

                    // 启动接收数据的任务
                    _receiveTask = Task.Run(ReceiveDataAsync, _cts.Token);

                    Log.Information("已重新连接到设备 {DeviceName} ({IpAddress}:{Port})", _deviceName, _ipAddress, _port);
                    return; // 连接成功，退出重连循环
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "重新连接设备 {DeviceName} ({IpAddress}:{Port}) 失败，{Interval}秒后重试", 
                        _deviceName, _ipAddress, _port, ReconnectInterval / 1000);
                    
                    // 等待一段时间后重试
                    try
                    {
                        await Task.Delay(ReconnectInterval, _cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // 取消操作，退出重连循环
                        return;
                    }
                }
            }
        }, _cts.Token);
    }

    /// <summary>
    /// 发送数据到设备
    /// </summary>
    /// <param name="data">要发送的数据</param>
    public async Task SendAsync(byte[] data)
    {
        if (!_isConnected || _stream == null)
        {
            throw new InvalidOperationException("未连接到设备");
        }

        try
        {
            await _stream.WriteAsync(data);
            await _stream.FlushAsync();
        }
        catch (Exception ex)
        {
            _isConnected = false;
            _connectionStatusCallback(false);
            Log.Error(ex, "向设备 {DeviceName} 发送数据失败", _deviceName);
            
            // 启动自动重连
            if (_autoReconnect && !_disposed)
            {
                StartReconnectTask();
            }
            
            throw;
        }
    }

    /// <summary>
    /// 接收数据的任务
    /// </summary>
    private async Task ReceiveDataAsync()
    {
        var buffer = new byte[1024];

        while (!_cts.Token.IsCancellationRequested && _isConnected && _stream != null)
        {
            try
            {
                var bytesRead = await _stream.ReadAsync(buffer, _cts.Token);
                if (bytesRead == 0)
                {
                    // 连接已关闭
                    _isConnected = false;
                    _connectionStatusCallback(false);
                    
                    // 启动自动重连
                    if (_autoReconnect && !_disposed)
                    {
                        StartReconnectTask();
                    }
                    
                    break;
                }

                var data = new byte[bytesRead];
                Array.Copy(buffer, data, bytesRead);
                _dataReceivedCallback(data);
            }
            catch (OperationCanceledException)
            {
                // 取消操作，正常退出
                break;
            }
            catch (Exception ex)
            {
                _isConnected = false;
                _connectionStatusCallback(false);
                Log.Error(ex, "从设备 {DeviceName} 接收数据失败", _deviceName);
                
                // 启动自动重连
                if (_autoReconnect && !_disposed)
                {
                    StartReconnectTask();
                }
                
                break;
            }
        }
    }

    /// <summary>
    /// 断开与设备的连接
    /// </summary>
    private void Disconnect()
    {
        if (!_isConnected) return;

        _isConnected = false;
        _connectionStatusCallback(false);
        _stream?.Close();
        _client?.Close();
        Log.Information("已断开与设备 {DeviceName} 的连接", _deviceName);
    }

    /// <summary>
    /// 获取连接状态
    /// </summary>
    /// <returns>是否已连接</returns>
    public bool IsConnected()
    {
        return _isConnected;
    }

    /// <summary>
    /// 设置是否自动重连
    /// </summary>
    /// <param name="autoReconnect">是否自动重连</param>
    public void SetAutoReconnect(bool autoReconnect)
    {
        _autoReconnect = autoReconnect;
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    /// <param name="disposing">是否释放托管资源</param>
    private void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _autoReconnect = false; // 停止自动重连
            _cts.Cancel();
            Disconnect();
            
            // 等待接收任务完成
            if (_receiveTask is { IsCompleted: false })
            {
                try
                {
                    // 尝试等待任务完成，但设置超时以避免无限等待
                    _receiveTask.Wait(TimeSpan.FromSeconds(3));
                }
                catch (AggregateException)
                {
                    // 忽略任务取消异常
                }
            }
            
            // 等待重连任务完成
            if (_reconnectTask is { IsCompleted: false })
            {
                try
                {
                    // 尝试等待任务完成，但设置超时以避免无限等待
                    _reconnectTask.Wait(TimeSpan.FromSeconds(3));
                }
                catch (AggregateException)
                {
                    // 忽略任务取消异常
                }
            }
            
            _cts.Dispose();
            _stream?.Dispose();
            _client?.Dispose();
        }

        _disposed = true;
    }
}