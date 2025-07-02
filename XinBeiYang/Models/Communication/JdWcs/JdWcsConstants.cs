namespace XinBeiYang.Models.Communication.JdWcs;

/// <summary>
///     京东WCS通信常量
/// </summary>
public static class JdWcsConstants
{
    /// <summary>
    ///     魔数
    /// </summary>
    public const int MagicNumber = 20230101;

    /// <summary>
    ///     心跳间隔（毫秒）
    /// </summary>
    public const int HeartbeatInterval = 5000; // 5秒

    /// <summary>
    ///     连接超时时间（毫秒）
    /// </summary>
    public const int ConnectionTimeout = 30000; // 30秒，文档要求30秒不活跃则重连

    /// <summary>
    ///     重连间隔（毫秒）
    /// </summary>
    public const int ReconnectInterval = 5000; // 5秒

    /// <summary>
    ///     消息响应超时时间（毫秒）
    /// </summary>
    public const int MessageResponseTimeout = 300; // 300毫秒，文档推荐

    /// <summary>
    ///     消息重发最大次数
    /// </summary>
    public const int MaxRetryCount = 3; // 文档推荐重发3次

    /// <summary>
    ///     协议版本
    /// </summary>
    public const sbyte ProtocolVersion = 2; // 支持格口数量上限32767

    /// <summary>
    ///     ACK确认码 - 成功
    /// </summary>
    public const int AckSuccess = 1;

    /// <summary>
    ///     ACK确认码 - 失败
    /// </summary>
    public const int AckFailure = 0;
}