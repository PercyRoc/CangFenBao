using System.Reactive;

namespace WeiCiModule.Services;

/// <summary>
/// 模组带连接服务接口
/// </summary>
public interface IModuleConnectionService
{
    /// <summary>
    /// 连接状态
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// 连接状态变化事件
    /// </summary>
    event EventHandler<bool>? ConnectionStateChanged;

    /// <summary>
    /// PLC 触发信号的响应式流 (已包含服务器时间戳)
    /// </summary>
    IObservable<Timestamped<ushort>> TriggerSignalStream { get; }

    /// <summary>
    /// 启动TCP服务器
    /// </summary>
    /// <param name="ipAddress">监听IP地址</param>
    /// <param name="port">监听端口</param>
    /// <returns>启动是否成功</returns>
    Task<bool> StartServerAsync(string ipAddress, int port);

    /// <summary>
    /// 停止TCP服务器
    /// </summary>
    /// <returns></returns>
    Task StopServerAsync();

    /// <summary>
    /// 异步发送分拣指令到模组带
    /// </summary>
    Task SendSortingCommandAsync(ushort packageNumber, byte chute);
} 