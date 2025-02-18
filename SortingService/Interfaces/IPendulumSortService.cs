using CommonLibrary.Models;
using CommonLibrary.Models.Settings.Sort;

namespace SortingService.Interfaces;

public interface IPendulumSortService : IDisposable
{
    /// <summary>
    ///     设备连接状态变更事件
    /// </summary>
    event EventHandler<(string Name, bool Connected)> DeviceConnectionStatusChanged;

    /// <summary>
    ///     初始化分检服务
    /// </summary>
    /// <param name="configuration">分检配置</param>
    Task InitializeAsync(SortConfiguration configuration);

    /// <summary>
    ///     启动分检服务
    /// </summary>
    Task StartAsync();

    /// <summary>
    ///     停止分检服务
    /// </summary>
    Task StopAsync();

    /// <summary>
    ///     更新分检配置
    /// </summary>
    /// <param name="configuration">分检配置</param>
    Task UpdateConfigurationAsync(SortConfiguration configuration);

    /// <summary>
    ///     获取分检服务状态
    /// </summary>
    /// <returns>是否正在运行</returns>
    bool IsRunning();

    /// <summary>
    ///     处理收到的包裹信息
    /// </summary>
    /// <param name="package">包裹信息</param>
    void ProcessPackage(PackageInfo package);
}