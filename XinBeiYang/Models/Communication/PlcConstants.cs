namespace XinBeiYang.Models.Communication;

/// <summary>
///     PLC通讯协议常量
/// </summary>
internal static class PlcConstants
{
    /// <summary>
    ///     报文头起始字符
    /// </summary>
    public const byte StartHeader1 = 0x7E;

    public const byte StartHeader2 = 0x0A;

    /// <summary>
    ///     报文尾结束字符
    /// </summary>
    internal const byte EndTail1 = 0x0D;

    internal const byte EndTail2 = 0x0A;

    /// <summary>
    ///     心跳周期（毫秒）
    /// </summary>
    internal const int HeartbeatInterval = 4000;

    /// <summary>
    ///     心跳超时时间（毫秒）
    /// </summary>
    internal const int HeartbeatTimeout = 10000;

    /// <summary>
    ///     重试次数
    /// </summary>
    public const int MaxRetryCount = 3;

    /// <summary>
    ///     最大包长度
    /// </summary>
    internal const int MaxPacketLength = 65535;
}