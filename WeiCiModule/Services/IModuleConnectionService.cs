using Common.Models.Package;

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
    /// 处理收到的包裹信息
    /// </summary>
    /// <param name="package">包裹信息</param>
    void OnPackageReceived(PackageInfo package);
} 