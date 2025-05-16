using Common.Models.Package;

namespace SortingServices.Modules;

public interface IModuleConnectionService
{
    /// <summary>
    ///     启动TCP服务端
    /// </summary>
    /// <param name="ipAddress">IP地址</param>
    /// <param name="port">端口号</param>
    /// <returns>是否成功启动</returns>
    Task<bool> StartServerAsync(string ipAddress, int port);

    /// <summary>
    ///     停止TCP服务端
    /// </summary>
    Task StopServerAsync();

    /// <summary>
    ///     连接状态改变事件
    /// </summary>
    event EventHandler<bool> ConnectionStateChanged;

    /// <summary>
    ///     处理收到的包裹信息
    /// </summary>
    /// <param name="package">包裹信息</param>
    void OnPackageReceived(PackageInfo package);
}