namespace Common.Services.Devices;

/// <summary>
/// 硬件设备顶层接口
/// </summary>
public interface IDevice : IAsyncDisposable
{
    /// <summary>
    /// 异步启动设备
    /// </summary>
    /// <returns>表示异步启动操作的任务，任务结果为true表示启动成功，false表示失败。</returns>
    Task<bool> StartAsync();

    /// <summary>
    /// 异步停止设备
    /// </summary>
    /// <returns>表示异步停止操作的任务，任务结果为true表示停止成功，false表示失败。</returns>
    Task<bool> StopAsync();

    /// <summary>
    /// 获取设备当前连接状态
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// 设备连接状态改变时触发
    /// </summary>
    event EventHandler<(string DeviceName, bool IsConnected)> ConnectionChanged;
}