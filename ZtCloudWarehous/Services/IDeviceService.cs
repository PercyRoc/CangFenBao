using Presentation_ZtCloudWarehous.Models;

namespace Presentation_ZtCloudWarehous.Services;

/// <summary>
///     设备服务接口
/// </summary>
public interface IDeviceService
{
    /// <summary>
    ///     设备注册
    /// </summary>
    Task<DeviceResponse> RegisterAsync(DeviceRegisterRequest request);

    /// <summary>
    ///     设备上线通知
    /// </summary>
    Task<DeviceResponse> OnlineNotifyAsync(DeviceBaseRequest request);

    /// <summary>
    ///     设备下线通知
    /// </summary>
    Task<DeviceResponse> OfflineNotifyAsync(DeviceBaseRequest request);

    /// <summary>
    ///     设备在线通知
    /// </summary>
    Task<DeviceResponse> HeartbeatAsync(DeviceBaseRequest request);

    /// <summary>
    ///     设备动作数据同步
    /// </summary>
    Task<DeviceResponse> SyncActionDataAsync(DeviceActionRequest request);

    /// <summary>
    ///     业务数据同步
    /// </summary>
    Task<DeviceResponse> SyncBusinessDataAsync(BusinessDataRequest request);

    /// <summary>
    ///     启动设备服务
    /// </summary>
    Task StartAsync();

    /// <summary>
    ///     停止设备服务
    /// </summary>
    Task StopAsync();
}