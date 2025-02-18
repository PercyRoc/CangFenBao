using SortingService.Interfaces;

namespace SortingService.Services;

public class TcpClientService : ITcpClientService
{
    private TcpConnection? _connection;
    private bool _disposed;

    public bool IsConnected => _connection?.IsConnected ?? false;
    public string IpAddress => _connection?.IpAddress ?? string.Empty;
    public int Port => _connection?.Port ?? 0;

    public async Task ConnectAsync(string ipAddress, int port, int timeout = 5000)
    {
        if (IsConnected) return;

        _connection = new TcpConnection(ipAddress, port, $"{ipAddress}:{port}");
        await _connection.ConnectAsync(timeout);
    }

    public async Task DisconnectAsync()
    {
        if (_connection != null)
        {
            await _connection.DisconnectAsync();
            _connection.Dispose();
            _connection = null;
        }
    }

    public async Task SendAsync(byte[] data)
    {
        if (_connection == null || !IsConnected) throw new InvalidOperationException("未连接到服务器");

        await _connection.SendAsync(data);
    }

    public async Task<byte[]> ReceiveAsync()
    {
        if (_connection == null || !IsConnected) throw new InvalidOperationException("未连接到服务器");

        var tcs = new TaskCompletionSource<byte[]>();

        void OnDataReceived(object? sender, byte[] data)
        {
            tcs.TrySetResult(data);
        }

        try
        {
            _connection.DataReceived += OnDataReceived;
            return await tcs.Task;
        }
        finally
        {
            _connection.DataReceived -= OnDataReceived;
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
            _connection?.Dispose();
            _connection = null;
        }

        _disposed = true;
    }
}