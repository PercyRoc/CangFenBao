using System.Net.Sockets;
using Presentation_PlateTurnoverMachine.Models;

namespace Presentation_PlateTurnoverMachine.Services;

/// <summary>
/// TCP连接服务接口
/// </summary>
public interface ITcpConnectionService : IDisposable
{
    /// <summary>
    /// 触发光电连接状态改变事件
    /// </summary>
    event EventHandler<bool> TriggerPhotoelectricConnectionChanged;

    /// <summary>
    /// TCP模块连接状态改变事件
    /// </summary>
    /// <remarks>
    /// 事件参数包含：TCP模块配置和连接状态
    /// </remarks>
    event EventHandler<(TcpConnectionConfig Config, bool Connected)> TcpModuleConnectionChanged;

    /// <summary>
    /// 连接触发光电
    /// </summary>
    /// <param name="config">连接配置</param>
    /// <returns>连接是否成功</returns>
    Task<bool> ConnectTriggerPhotoelectricAsync(TcpConnectionConfig config);
    
    /// <summary>
    /// 连接TCP模块
    /// </summary>
    /// <param name="configs">TCP模块连接配置列表</param>
    /// <returns>连接结果字典，key为配置，value为对应的TcpClient</returns>
    Task<Dictionary<TcpConnectionConfig, TcpClient>> ConnectTcpModulesAsync(IEnumerable<TcpConnectionConfig> configs);
    
    /// <summary>
    /// 获取触发光电的TCP客户端
    /// </summary>
    TcpClient? TriggerPhotoelectricClient { get; }
    
    /// <summary>
    /// 获取TCP模块客户端字典
    /// </summary>
    IReadOnlyDictionary<TcpConnectionConfig, TcpClient> TcpModuleClients { get; }
    /// <summary>
    /// 发送数据到指定的TCP模块
    /// </summary>
    /// <param name="config">TCP模块配置</param>
    /// <param name="data">要发送的数据</param>
    Task SendToTcpModuleAsync(TcpConnectionConfig config, byte[] data);
    
    /// <summary>
    /// 触发光电数据接收事件
    /// </summary>
    event EventHandler<TcpDataReceivedEventArgs> TriggerPhotoelectricDataReceived;
    
    /// <summary>
    /// TCP模块数据接收事件
    /// </summary>
    event EventHandler<TcpModuleDataReceivedEventArgs> TcpModuleDataReceived;
} 