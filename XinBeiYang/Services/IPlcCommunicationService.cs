namespace XinBeiYang.Services;

using Models.Communication;

/// <summary>
///     PLC通讯服务接口
/// </summary>
public interface IPlcCommunicationService : IDisposable
{
    /// <summary>
    ///     获取当前连接状态
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    ///     设备状态变更事件
    /// </summary>
    event EventHandler<DeviceStatusCode> DeviceStatusChanged;

    /// <summary>
    ///     上包最终结果事件
    /// </summary>
    event EventHandler<(ushort CommandId, bool IsTimeout, int PackageId)> UploadResultReceived;

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
    /// <param name="weight">重量。</param>
    /// <param name="length">长度。</param>
    /// <param name="width">宽度。</param>
    /// <param name="height">高度。</param>
    /// <param name="barcode1D">一维码。</param>
    /// <param name="barcode2D">二维码。</param>
    /// <param name="scanTimestamp">扫描时间戳。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>一个元组，包含是否接受了请求以及命令ID。</returns>
    Task<(bool IsAccepted, ushort CommandId)> SendUploadRequestAsync(float weight, float length, float width, float height,
        string barcode1D, string barcode2D, ulong scanTimestamp, CancellationToken cancellationToken);

    /// <summary>
    /// 等待指定命令ID的上包最终结果。
    /// </summary>
    /// <param name="commandId">要等待结果的命令ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>一个元组，包含操作是否成功（非超时）、是否超时以及PLC分配的包裹ID。</returns>
    Task<(bool WasSuccess, bool IsTimeout, int PackageId)> WaitForUploadResultAsync(ushort commandId, CancellationToken cancellationToken);
}