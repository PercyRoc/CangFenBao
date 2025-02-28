namespace Presentation_XinBeiYang.Models.Communication.Packets;

/// <summary>
/// 心跳数据包
/// </summary>
public class HeartbeatPacket(ushort commandId) : PlcPacket(CommandType.Heartbeat, commandId)
{
    protected override byte[]? GetMessageBody()
    {
        return null;
    }
}

/// <summary>
/// 心跳应答数据包
/// </summary>
public class HeartbeatAckPacket(ushort commandId) : PlcPacket(CommandType.HeartbeatAck, commandId)
{
    protected override byte[]? GetMessageBody()
    {
        return null;
    }
} 