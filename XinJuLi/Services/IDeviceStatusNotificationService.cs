using System;
using System.Threading.Tasks;

namespace XinJuLi.Services;

/// <summary>
/// 设备状态通知服务接口
/// </summary>
public interface IDeviceStatusNotificationService
{
    /// <summary>
    /// 启动WebSocket服务器
    /// </summary>
    /// <param name="port">端口号，默认8080</param>
    /// <returns>启动任务</returns>
    Task StartAsync(int port = 8080);

    /// <summary>
    /// 停止WebSocket服务器
    /// </summary>
    /// <returns>停止任务</returns>
    Task StopAsync();

    /// <summary>
    /// 通知设备上线
    /// </summary>
    /// <param name="deviceId">设备ID</param>
    /// <returns>通知任务</returns>
    Task NotifyDeviceOnlineAsync(string deviceId);

    /// <summary>
    /// 通知设备下线
    /// </summary>
    /// <param name="deviceId">设备ID</param>
    /// <returns>通知任务</returns>
    Task NotifyDeviceOfflineAsync(string deviceId);

    /// <summary>
    /// 发送包裹生成通知
    /// </summary>
    /// <param name="packageId">包裹ID</param>
    /// <param name="sourceDeviceId">源设备ID</param>
    /// <param name="sortCode">格口号</param>
    /// <returns>通知任务</returns>
    Task NotifyPackageCreatedAsync(string packageId, string sourceDeviceId, int sortCode);

    /// <summary>
    /// 获取WebSocket服务器运行状态
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// 获取连接的客户端数量
    /// </summary>
    int ConnectedClientsCount { get; }

    /// <summary>
    /// 服务器启动事件
    /// </summary>
    event EventHandler<int>? ServerStarted;

    /// <summary>
    /// 服务器停止事件
    /// </summary>
    event EventHandler? ServerStopped;

    /// <summary>
    /// 客户端连接事件
    /// </summary>
    event EventHandler<string>? ClientConnected;

    /// <summary>
    /// 客户端断开事件
    /// </summary>
    event EventHandler<string>? ClientDisconnected;
} 