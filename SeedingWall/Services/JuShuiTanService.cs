using System.Text.Json;
using System.Windows.Threading;
using Presentation_SeedingWall.Models;
using Serilog;
using WebSocketSharp;

namespace Presentation_SeedingWall.Services;

/// <summary>
///     聚水潭WebSocket通信服务实现
/// </summary>
internal class JuShuiTanService : IJuShuiTanService
{
    private readonly Dispatcher _dispatcher;
    private bool _disposed;
    private WebSocket? _webSocket;

    /// <summary>
    ///     构造函数
    /// </summary>
    public JuShuiTanService()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    /// <summary>
    ///     WebSocket连接状态变更事件
    /// </summary>
    public event Action<bool>? ConnectionStatusChanged;

    /// <summary>
    ///     收到消息事件
    /// </summary>
    public event Action<string>? MessageReceived;

    /// <summary>
    ///     错误事件
    /// </summary>
    public event Action<Exception>? ErrorOccurred;

    /// <summary>
    ///     是否已连接
    /// </summary>
    public bool IsConnected => _webSocket?.ReadyState == WebSocketState.Open;

    /// <summary>
    ///     连接到WebSocket服务器
    /// </summary>
    /// <param name="url">WebSocket服务器地址</param>
    /// <returns>连接结果</returns>
    public async Task<bool> ConnectAsync(string url)
    {
        try
        {
            // 如果已经连接，先断开
            Disconnect();

            Log.Information("正在连接到聚水潭WebSocket服务器: {Url}", url);

            _webSocket = new WebSocket(url);

            // 注册事件处理
            _webSocket.OnOpen += (_, _) =>
            {
                Log.Information("已连接到聚水潭WebSocket服务器");
                _dispatcher.BeginInvoke(() => ConnectionStatusChanged?.Invoke(true));
            };

            _webSocket.OnClose += (_, e) =>
            {
                Log.Information("与聚水潭WebSocket服务器的连接已关闭: {Code}, {Reason}", e.Code, e.Reason);
                _dispatcher.BeginInvoke(() => ConnectionStatusChanged?.Invoke(false));
            };

            _webSocket.OnMessage += (_, e) =>
            {
                Log.Debug("收到聚水潭WebSocket消息: {Data}", e.Data);
                _dispatcher.BeginInvoke(() => MessageReceived?.Invoke(e.Data));
            };

            _webSocket.OnError += (_, e) =>
            {
                Log.Error(e.Exception, "聚水潭WebSocket连接发生错误");
                _dispatcher.BeginInvoke(() => ErrorOccurred?.Invoke(e.Exception));
            };

            // 连接到服务器
            _webSocket.ConnectAsync();

            // 等待连接建立或超时
            var connectTask = Task.Run(() =>
            {
                var startTime = DateTime.Now;
                while (_webSocket.ReadyState != WebSocketState.Open)
                {
                    if (DateTime.Now - startTime > TimeSpan.FromSeconds(10)) return false;

                    if (_webSocket.ReadyState == WebSocketState.Closed) return false;

                    Thread.Sleep(100);
                }

                return true;
            });

            return await connectTask;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "连接到聚水潭WebSocket服务器时发生错误");
            await _dispatcher.BeginInvoke(() => ErrorOccurred?.Invoke(ex));
            return false;
        }
    }

    /// <summary>
    ///     断开WebSocket连接
    /// </summary>
    public void Disconnect()
    {
        if (_webSocket == null) return;

        try
        {
            if (_webSocket.ReadyState == WebSocketState.Open)
            {
                Log.Information("正在断开与聚水潭WebSocket服务器的连接");
                _webSocket.Close(CloseStatusCode.Normal, "客户端主动断开连接");
            }

            _webSocket = null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "断开与聚水潭WebSocket服务器的连接时发生错误");
        }
    }

    /// <summary>
    ///     发送播种验货请求
    /// </summary>
    /// <param name="request">播种验货请求</param>
    /// <returns>发送结果</returns>
    public Task<bool> SendSeedingVerificationAsync(SeedingVerificationRequest request)
    {
        try
        {
            if (_webSocket?.ReadyState != WebSocketState.Open)
            {
                Log.Warning("无法发送播种验货请求，WebSocket未连接");
                return Task.FromResult(false);
            }

            var json = JsonSerializer.Serialize(request);
            Log.Debug("发送播种验货请求: {Json}", json);

            _webSocket.Send(json);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "发送播种验货请求时发生错误");
            _dispatcher.BeginInvoke(() => ErrorOccurred?.Invoke(ex));
            return Task.FromResult(false);
        }
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
    ///     释放资源
    /// </summary>
    /// <param name="disposing">是否正在释放托管资源</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing) Disconnect();

        _disposed = true;
    }
}