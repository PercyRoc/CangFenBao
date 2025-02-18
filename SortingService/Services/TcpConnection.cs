using System.Net.Sockets;
using Serilog;

namespace SortingService.Services;

/// <summary>
///     TCP连接基础类，提供基本的连接、断开、收发数据功能
/// </summary>
public class TcpConnection(string ipAddress, int port, string connectionId) : IDisposable
{
    private readonly byte[] _buffer = new byte[1024];
    private TcpClient? _client;
    private bool _disposed;
    private CancellationTokenSource? _receiveCts;
    private NetworkStream? _stream;

    public bool IsConnected => _client?.Connected ?? false;
    public string IpAddress { get; } = ipAddress;
    public int Port { get; } = port;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     数据接收事件
    /// </summary>
    public event EventHandler<byte[]>? DataReceived;

    public async Task ConnectAsync(int timeout = 5000)
    {
        if (IsConnected) return;

        try
        {
            _client = new TcpClient();
            using var cts = new CancellationTokenSource(timeout);
            await _client.ConnectAsync(IpAddress, Port, cts.Token);
            _stream = _client.GetStream();

            // 启动接收循环
            _receiveCts = new CancellationTokenSource();
            _ = Task.Run(ReceiveLoop, _receiveCts.Token);

            Log.Information("TCP连接已建立: {ConnectionId}", connectionId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "TCP连接失败: {ConnectionId}", connectionId);
            await DisconnectAsync();
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        try
        {
            // 停止接收循环
            if (_receiveCts != null)
            {
                await _receiveCts.CancelAsync();
                _receiveCts.Dispose();
                _receiveCts = null;
            }

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

            Log.Information("TCP连接已断开: {ConnectionId}", connectionId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "断开TCP连接时发生错误: {ConnectionId}", connectionId);
            throw;
        }
    }

    public async Task SendAsync(byte[] data)
    {
        if (_stream == null || !IsConnected) throw new InvalidOperationException($"未连接到服务器: {connectionId}");

        try
        {
            await _stream.WriteAsync(data);
            await _stream.FlushAsync();
            Log.Debug("数据已发送: {ConnectionId}, 长度={Length}字节", connectionId, data.Length);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "发送数据时发生错误: {ConnectionId}", connectionId);
            throw;
        }
    }

    private async Task ReceiveLoop()
    {
        while (!_receiveCts?.Token.IsCancellationRequested ?? false)
            try
            {
                if (_stream == null || !IsConnected)
                {
                    await Task.Delay(100, _receiveCts?.Token ?? CancellationToken.None);
                    continue;
                }

                var bytesRead = await _stream.ReadAsync(_buffer, _receiveCts?.Token ?? CancellationToken.None);
                if (bytesRead == 0)
                {
                    Log.Warning("连接已关闭: {ConnectionId}", connectionId);
                    break;
                }

                var data = new byte[bytesRead];
                Array.Copy(_buffer, data, bytesRead);

                Log.Debug("收到数据: {ConnectionId}, 长度={Length}字节", connectionId, bytesRead);
                DataReceived?.Invoke(this, data);
            }
            catch (OperationCanceledException)
            {
                // 正常取消，不做处理
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "接收数据时发生错误: {ConnectionId}", connectionId);
                await Task.Delay(1000, _receiveCts?.Token ?? CancellationToken.None);
            }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _receiveCts?.Dispose();
            _stream?.Dispose();
            _client?.Dispose();
        }

        _disposed = true;
    }
}