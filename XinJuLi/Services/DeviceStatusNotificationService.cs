using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Tasks;
using Serilog;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace XinJuLi.Services;

/// <summary>
/// 设备状态通知服务实现
/// </summary>
public class DeviceStatusNotificationService : IDeviceStatusNotificationService, IDisposable
{
    private WebSocketServer? _webSocketServer;
    private readonly ConcurrentDictionary<string, DeviceStatusWebSocketBehavior> _sessions = new();
    private bool _disposed;

    public bool IsRunning => _webSocketServer?.IsListening ?? false;
    public int ConnectedClientsCount => _sessions.Count;

    public event EventHandler<int>? ServerStarted;
    public event EventHandler? ServerStopped;
    public event EventHandler<string>? ClientConnected;
    public event EventHandler<string>? ClientDisconnected;

    /// <summary>
    /// 启动WebSocket服务器
    /// </summary>
    public async Task StartAsync(int port = 8080)
    {
        try
        {
            if (IsRunning)
            {
                Log.Warning("WebSocket服务器已在运行，端口: {Port}", port);
                return;
            }

            await Task.Run(() =>
            {
                _webSocketServer = new WebSocketServer($"ws://localhost:{port}");
                _webSocketServer.AddWebSocketService<DeviceStatusWebSocketBehavior>("/device-status", () =>
                {
                    var behavior = new DeviceStatusWebSocketBehavior();
                    behavior.SessionAdded += OnSessionAdded;
                    behavior.SessionRemoved += OnSessionRemoved;
                    return behavior;
                });

                _webSocketServer.Start();
                
                Log.Information("WebSocket服务器已启动，端口: {Port}", port);
                ServerStarted?.Invoke(this, port);
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动WebSocket服务器失败，端口: {Port}", port);
            throw;
        }
    }

    /// <summary>
    /// 停止WebSocket服务器
    /// </summary>
    public async Task StopAsync()
    {
        try
        {
            if (!IsRunning)
            {
                Log.Information("WebSocket服务器未运行，无需停止");
                return;
            }

            await Task.Run(() =>
            {
                _webSocketServer?.Stop();
                _sessions.Clear();
                
                Log.Information("WebSocket服务器已停止");
                ServerStopped?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止WebSocket服务器失败");
            throw;
        }
    }

    /// <summary>
    /// 通知设备上线
    /// </summary>
    public async Task NotifyDeviceOnlineAsync(string deviceId)
    {
        await SendDeviceStatusAsync(deviceId, "ONLINE");
    }

    /// <summary>
    /// 通知设备下线
    /// </summary>
    public async Task NotifyDeviceOfflineAsync(string deviceId)
    {
        await SendDeviceStatusAsync(deviceId, "OFFLINE");
    }

    /// <summary>
    /// 发送包裹生成通知
    /// </summary>
    public async Task NotifyPackageCreatedAsync(string packageId, string sourceDeviceId, int sortCode)
    {
        await SendPackageReportAsync(packageId, sourceDeviceId, sortCode);
    }

    /// <summary>
    /// 发送设备状态消息
    /// </summary>
    private async Task SendDeviceStatusAsync(string deviceId, string status)
    {
        try
        {
            if (!IsRunning)
            {
                Log.Warning("WebSocket服务器未运行，无法发送设备状态: {DeviceId} -> {Status}", deviceId, status);
                return;
            }

            var message = new
            {
                type = "deviceStatus",
                data = new
                {
                    deviceId = deviceId,
                    status = status,
                    timestamp = DateTime.UtcNow.ToString("o") // ISO 8601格式
                }
            };

            var jsonMessage = JsonSerializer.Serialize(message, new JsonSerializerOptions 
            { 
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
            });

            await Task.Run(() =>
            {
                foreach (var session in _sessions.Values)
                {
                    try
                    {
                        session.SendMessage(jsonMessage);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "发送消息到客户端失败: {SessionId}", session.ID);
                    }
                }
            });

            Log.Information("设备状态已发送: {DeviceId} -> {Status}, 客户端数量: {Count}", 
                deviceId, status, ConnectedClientsCount);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "发送设备状态失败: {DeviceId} -> {Status}", deviceId, status);
        }
    }

    /// <summary>
    /// 发送包裹报告消息
    /// </summary>
    private async Task SendPackageReportAsync(string packageId, string sourceDeviceId, int sortCode)
    {
        try
        {
            if (!IsRunning)
            {
                Log.Warning("WebSocket服务器未运行，无法发送包裹报告: {PackageId} -> {SortCode}", packageId, sortCode);
                return;
            }

            var message = new
            {
                type = "packageReport",
                data = new
                {
                    packageId = packageId,
                    sourceDeviceId = sourceDeviceId,
                    sortCode = sortCode,
                    timestamp = DateTime.UtcNow.ToString("o") // ISO 8601格式
                }
            };

            var jsonMessage = JsonSerializer.Serialize(message, new JsonSerializerOptions 
            { 
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
            });

            await Task.Run(() =>
            {
                foreach (var session in _sessions.Values)
                {
                    try
                    {
                        session.SendMessage(jsonMessage);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "发送消息到客户端失败: {SessionId}", session.ID);
                    }
                }
            });

            Log.Information("包裹报告已发送: 包裹ID={PackageId}, 源设备={SourceDeviceId}, 格口={SortCode}, 客户端数量={Count}", 
                packageId, sourceDeviceId, sortCode, ConnectedClientsCount);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "发送包裹报告失败: 包裹ID={PackageId}, 源设备={SourceDeviceId}, 格口={SortCode}", 
                packageId, sourceDeviceId, sortCode);
        }
    }

    /// <summary>
    /// 会话添加事件处理
    /// </summary>
    private void OnSessionAdded(object? sender, string sessionId)
    {
        if (sender is DeviceStatusWebSocketBehavior behavior)
        {
            _sessions.TryAdd(sessionId, behavior);
            Log.Information("客户端已连接: {SessionId}, 总连接数: {Count}", sessionId, ConnectedClientsCount);
            ClientConnected?.Invoke(this, sessionId);
        }
    }

    /// <summary>
    /// 会话移除事件处理
    /// </summary>
    private void OnSessionRemoved(object? sender, string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
        Log.Information("客户端已断开: {SessionId}, 总连接数: {Count}", sessionId, ConnectedClientsCount);
        ClientDisconnected?.Invoke(this, sessionId);
    }

    /// <summary>
    /// 释放资源
    /// </summary>
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
                StopAsync().Wait(5000);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "释放WebSocket服务器资源时发生错误");
            }

            _webSocketServer?.Stop();
            _sessions.Clear();
        }

        _disposed = true;
    }
}

/// <summary>
/// 设备状态WebSocket行为类
/// </summary>
public class DeviceStatusWebSocketBehavior : WebSocketBehavior
{
    public event EventHandler<string>? SessionAdded;
    public event EventHandler<string>? SessionRemoved;

    protected override void OnOpen()
    {
        // Log.Information($"WebSocket连接已建立: {ID}");
        SessionAdded?.Invoke(this, ID);
    }

    protected override void OnClose(CloseEventArgs e)
    {
        // Log.Information($"WebSocket连接已关闭: {ID}, 原因: {e.Reason ?? "未知原因"}");
        SessionRemoved?.Invoke(this, ID);
    }

    protected override void OnError(ErrorEventArgs e)
    {
        // Log.Error($"WebSocket连接发生错误: {ID}, 错误: {e.Message ?? "未知错误"}");
    }

    protected override void OnMessage(MessageEventArgs e)
    {
        // Log.Information($"收到WebSocket消息: {ID}, 内容: {e.Data ?? "空数据"}");
        // 这里可以处理来自前端的消息，比如心跳包等
    }

    /// <summary>
    /// 发送消息到客户端
    /// </summary>
    public void SendMessage(string message)
    {
        if (State == WebSocketState.Open)
        {
            Send(message);
        }
    }
} 