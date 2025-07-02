namespace XinBeiYang.Models.Communication;

/// <summary>
///     设备状态码
/// </summary>
public enum DeviceStatusCode : byte
{
    /// <summary>
    ///     正常
    /// </summary>
    Normal = 0x00,

    /// <summary>
    ///     上位机禁用
    /// </summary>
    Disabled = 0x01,

    /// <summary>
    ///     灰度仪异常
    /// </summary>
    GrayscaleError = 0x02,

    /// <summary>
    ///     主线停机
    /// </summary>
    MainLineStopped = 0x03,

    /// <summary>
    ///     主线故障
    /// </summary>
    MainLineFault = 0x04,

    /// <summary>
    ///     设备未连接
    /// </summary>
    Disconnected = 0xFF
}