using System.Net.Sockets;
using SortingService.Interfaces;
using Serilog;

namespace SortingService.Services;

public class TcpClientService : ITcpClientService
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    private bool _disposed;
    private string _ipAddress = string.Empty;
    private int _port;

    public bool IsConnected => _client?.Connected ?? false;
    public string IpAddress => _ipAddress;
    public int Port => _port;

    public async Task ConnectAsync(string ipAddress, int port, int timeout = 5000)
    {
        if (IsConnected) return;

        _ipAddress = ipAddress;
        _port = port;

        try
        {
            Log.Information("正在连接到 {IpAddress}:{Port}...", ipAddress, port);
            
            _client = new TcpClient
            {
                NoDelay = true,
                ReceiveTimeout = timeout,
                SendTimeout = timeout
            };

            using var cts = new CancellationTokenSource(timeout);
            await _client.ConnectAsync(ipAddress, port, cts.Token);
            _stream = _client.GetStream();
            
            Log.Information("已成功连接到 {IpAddress}:{Port}", ipAddress, port);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "连接到 {IpAddress}:{Port} 失败", ipAddress, port);
            await DisconnectAsync();
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        try
        {
            if (_stream != null)
            {
                await _stream.DisposeAsync();
                _stream = null;
            }

            if (_client != null)
            {
                _client.Dispose();
                _client = null;
            }

            // 重置连接信息
            var oldIp = _ipAddress;
            var oldPort = _port;
            _ipAddress = string.Empty;
            _port = 0;

            Log.Information("已断开与 {IpAddress}:{Port} 的连接", oldIp, oldPort);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "断开连接时发生错误");
            throw;
        }
    }

    public async Task SendAsync(byte[] data)
    {
        if (_stream == null || !IsConnected)
            throw new InvalidOperationException($"未连接到服务器 {_ipAddress}:{_port}");

        try
        {
            await _stream.WriteAsync(data);
            await _stream.FlushAsync();
            Log.Debug("已发送 {Length} 字节数据到 {IpAddress}:{Port}", data.Length, _ipAddress, _port);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "发送数据到 {IpAddress}:{Port} 失败", _ipAddress, _port);
            throw;
        }
    }

    public async Task<byte[]> ReceiveAsync()
    {
        if (_stream == null || !IsConnected)
            throw new InvalidOperationException($"未连接到服务器 {_ipAddress}:{_port}");

        try
        {
            var buffer = new byte[1024];
            var bytesRead = await _stream.ReadAsync(buffer);
            
            if (bytesRead == 0)
            {
                Log.Warning("连接已关闭: {IpAddress}:{Port}", _ipAddress, _port);
                throw new InvalidOperationException("连接已关闭");
            }

            var data = new byte[bytesRead];
            Array.Copy(buffer, data, bytesRead);
            
            Log.Debug("从 {IpAddress}:{Port} 接收到 {Length} 字节数据", _ipAddress, _port, bytesRead);
            return data;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "从 {IpAddress}:{Port} 接收数据失败", _ipAddress, _port);
            throw;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            try
            {
                // 检查是否需要断开连接
                if (IsConnected)
                {
                    Log.Warning("检测到未断开的连接，正在强制释放资源: {IpAddress}:{Port}", _ipAddress, _port);
                    DisconnectAsync().Wait();
                }

                _stream?.Dispose();
                _client?.Dispose();

                // 清理资源引用
                _stream = null;
                _client = null;
                _ipAddress = string.Empty;
                _port = 0;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "释放TCP客户端资源时发生错误");
            }
        }

        _disposed = true;
    }
}