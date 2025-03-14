namespace XinBeiYang.Models.Communication.Packets;

/// <summary>
///     设备状态数据包
/// </summary>
internal class DeviceStatusPacket : PlcPacket
{
    private DeviceStatusPacket(ushort commandId, DeviceStatusCode statusCode)
        : base(CommandType.DeviceStatus, commandId)
    {
        StatusCode = statusCode;
    }

    /// <summary>
    ///     设备状态码
    /// </summary>
    internal DeviceStatusCode StatusCode { get; }

    protected override byte[] GetMessageBody()
    {
        return [0x00, (byte)StatusCode];
    }

    internal static DeviceStatusPacket Parse(ushort commandId, ReadOnlySpan<byte> data)
    {
        var statusCode = (DeviceStatusCode)data[1];
        return new DeviceStatusPacket(commandId, statusCode);
    }
}

/// <summary>
///     设备状态应答数据包
/// </summary>
internal class DeviceStatusAckPacket(ushort commandId) : PlcPacket(CommandType.DeviceStatusAck, commandId)
{
    protected override byte[]? GetMessageBody()
    {
        return null;
    }
}