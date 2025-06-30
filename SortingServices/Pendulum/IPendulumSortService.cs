using Common.Models.Package;
using Common.Models.Settings.Sort.PendulumSort;

namespace SortingServices.Pendulum;

public interface IPendulumSortService : IDisposable
{
    /// <summary>
    ///     设备连接状态变更事件
    /// </summary>
    event EventHandler<(string Name, bool Connected)> DeviceConnectionStatusChanged;

    /// <summary>
    ///     触发光电信号事件
    /// </summary>
    event EventHandler<DateTime> TriggerPhotoelectricSignal;

    /// <summary>
    ///     分拣光电信号事件
    /// </summary>
    event EventHandler<(string PhotoelectricName, DateTime SignalTime)> SortingPhotoelectricSignal;

    /// <summary>
    ///     初始化分检服务
    /// </summary>
    /// <param name="configuration">分检配置</param>
    Task InitializeAsync(PendulumSortConfig configuration);

    /// <summary>
    ///     启动分检服务
    /// </summary>
    Task StartAsync();

    /// <summary>
    ///     停止分检服务
    /// </summary>
    Task StopAsync();

    /// <summary>
    ///     获取分检服务状态
    /// </summary>
    /// <returns>是否正在运行</returns>
    bool IsRunning();

    /// <summary>
    ///     处理包裹信息
    /// </summary>
    /// <param name="package">包裹信息</param>
    void ProcessPackage(PackageInfo package);

    /// <summary>
    ///     获取所有设备的连接状态
    /// </summary>
    /// <returns>设备名称和连接状态的字典</returns>
    Dictionary<string, bool> GetAllDeviceConnectionStates();
}