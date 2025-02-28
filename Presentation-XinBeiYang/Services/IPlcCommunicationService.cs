using Presentation_XinBeiYang.Models.Communication;
using Presentation_XinBeiYang.Models.Communication.Packets;

namespace Presentation_XinBeiYang.Services;

/// <summary>
/// PLC通讯服务接口
/// </summary>
public interface IPlcCommunicationService
{
    /// <summary>
    /// 连接状态变更事件
    /// </summary>
    event EventHandler<bool> ConnectionStatusChanged;

    /// <summary>
    /// 设备状态变更事件
    /// </summary>
    event EventHandler<DeviceStatusCode> DeviceStatusChanged;

    /// <summary>
    /// 上包结果事件
    /// </summary>
    event EventHandler<(bool IsTimeout, int PackageId)> UploadResultReceived;

    /// <summary>
    /// 连接到PLC
    /// </summary>
    Task ConnectAsync(string ipAddress, int port);

    /// <summary>
    /// 断开连接
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    /// 发送上包请求
    /// </summary>
    Task<bool> SendUploadRequestAsync(float weight, float length, float width, float height,
        string barcode1D, string barcode2D, ulong scanTimestamp);

    /// <summary>
    /// 获取当前连接状态
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// 获取当前设备状态
    /// </summary>
    DeviceStatusCode CurrentDeviceStatus { get; }
} 