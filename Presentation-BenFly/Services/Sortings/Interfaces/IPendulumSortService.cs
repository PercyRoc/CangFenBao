using CommonLibrary.Models;
using CommonLibrary.Models.Settings.Sort;

namespace Presentation_BenFly.Services.Sortings.Interfaces;

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

    /// <summary>
    ///     获取设备连接状态
    /// </summary>
    /// <param name="deviceName">设备名称</param>
    /// <returns>true表示已连接，false表示未连接</returns>
    bool GetDeviceConnectionState(string deviceName);

    /// <summary>
    ///     获取所有设备的连接状态
    /// </summary>
    /// <returns>设备名称和连接状态的字典</returns>
    Dictionary<string, bool> GetAllDeviceConnectionStates();
}