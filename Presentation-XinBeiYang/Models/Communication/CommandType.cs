namespace Presentation_XinBeiYang.Models.Communication;

/// <summary>
///     PLC通讯指令类型
/// </summary>
public enum CommandType : ushort
{
    /// <summary>
    ///     PC→PLC心跳
    /// </summary>
    Heartbeat = 0x0010,

    /// <summary>
    ///     PLC→PC心跳ACK
    /// </summary>
    HeartbeatAck = 0x0011,

    /// <summary>
    ///     快手→PLC上包请求
    /// </summary>
    UploadRequest = 0x0020,

    /// <summary>
    ///     PLC→快手上包请求ACK
    /// </summary>
    UploadRequestAck = 0x0021,

    /// <summary>
    ///     PLC→快手上包结果
    /// </summary>
    UploadResult = 0x0030,

    /// <summary>
    ///     快手→PLC上包结果ACK
    /// </summary>
    UploadResultAck = 0x0031,

    /// <summary>
    ///     PLC→快手设备状态
    /// </summary>
    DeviceStatus = 0x0060,

    /// <summary>
    ///     快手→PLC设备状态ACK
    /// </summary>
    DeviceStatusAck = 0x0061
}