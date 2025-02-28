namespace Presentation_XinBeiYang.Models.Communication.Packets;

/// <summary>
/// 设备状态数据包
/// </summary>
public class DeviceStatusPacket : PlcPacket
{
    /// <summary>
    /// 设备状态码
    /// </summary>
    public DeviceStatusCode StatusCode { get; }

    public DeviceStatusPacket(ushort commandId, DeviceStatusCode statusCode)
        : base(CommandType.DeviceStatus, commandId)
    {
        StatusCode = statusCode;
    }

    protected override byte[] GetMessageBody()
    {
        return new byte[] { 0x00, (byte)StatusCode };
    }

    public static DeviceStatusPacket Parse(ushort commandId, ReadOnlySpan<byte> data)
    {
        var statusCode = (DeviceStatusCode)data[1];
        return new DeviceStatusPacket(commandId, statusCode);
    }
}

/// <summary>
/// 设备状态应答数据包
/// </summary>
public class DeviceStatusAckPacket : PlcPacket
{
    public DeviceStatusAckPacket(ushort commandId)
        : base(CommandType.DeviceStatusAck, commandId)
    {
    }

    protected override byte[]? GetMessageBody()
    {
        return null;
    }
} 