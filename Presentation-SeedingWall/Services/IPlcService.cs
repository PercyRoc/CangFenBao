using Presentation_SeedingWall.Models;

namespace Presentation_SeedingWall.Services;

/// <summary>
/// PLC通信服务接口
/// </summary>
public interface IPlcService : IDisposable
{
    /// <summary>
    /// 连接状态变更事件
    /// </summary>
    event Action<bool> ConnectionStatusChanged;
    
    /// <summary>
    /// 收到PLC指令事件
    /// </summary>
    event Action<PlcCommand> CommandReceived;
    
    /// <summary>
    /// 错误事件
    /// </summary>
    event Action<Exception> ErrorOccurred;
    
    /// <summary>
    /// 是否已连接
    /// </summary>
    bool IsConnected { get; }
    
    /// <summary>
    /// 启动TCP服务器
    /// </summary>
    /// <param name="ip">服务器IP地址</param>
    /// <param name="port">服务器端口</param>
    /// <returns>启动结果</returns>
    Task<bool> StartServerAsync(string ip, int port);
    
    /// <summary>
    /// 停止TCP服务器
    /// </summary>
    void StopServer();
    
    /// <summary>
    /// 发送分拣指令
    /// </summary>
    /// <param name="packageNumber">包裹号</param>
    /// <param name="slotNumber">格口号</param>
    /// <returns>发送结果</returns>
    Task<bool> SendSortingCommandAsync(int packageNumber, int slotNumber);
} 