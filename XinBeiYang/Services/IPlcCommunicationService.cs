namespace XinBeiYang.Services;

using Models.Communication;

/// <summary>
///     PLC通讯服务接口
/// </summary>
public interface IPlcCommunicationService
{
    /// <summary>
    ///     获取当前连接状态
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    ///     连接状态变更事件
    /// </summary>
    event EventHandler<bool> ConnectionStatusChanged;
    
    /// <summary>
    ///     设备状态变更事件
    /// </summary>
    event EventHandler<DeviceStatusCode> DeviceStatusChanged;

    /// <summary>
    ///     连接到PLC
    /// </summary>
    Task ConnectAsync(string ipAddress, int port);

    /// <summary>
    ///     断开连接
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    ///     发送上包请求
    /// </summary>
    /// <returns>
    ///     - IsSuccess: 是否成功
    ///     - IsTimeout: 是否超时
    ///     - CommandId: 指令ID
    ///     - PackageId: 包裹ID
    /// </returns>
    Task<(bool IsSuccess, bool IsTimeout, ushort CommandId, int PackageId)> SendUploadRequestAsync(float weight, float length, float width, float height,
        string barcode1D, string barcode2D, ulong scanTimestamp);
}