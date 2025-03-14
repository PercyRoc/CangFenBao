using System.Buffers.Binary;

namespace XinBeiYang.Models.Communication.Packets;

/// <summary>
///     上包结果数据包
/// </summary>
internal class UploadResultPacket : PlcPacket
{
    private UploadResultPacket(ushort commandId, bool isTimeout, int packageId)
        : base(CommandType.UploadResult, commandId)
    {
        IsTimeout = isTimeout;
        PackageId = packageId;
    }

    /// <summary>
    ///     上包结果
    /// </summary>
    internal bool IsTimeout { get; }

    /// <summary>
    ///     包裹流水号
    /// </summary>
    internal int PackageId { get; }

    protected override byte[] GetMessageBody()
    {
        var buffer = new byte[6];
        var span = buffer.AsSpan();

        // 写入上包结果
        BinaryPrimitives.WriteUInt16BigEndian(span, IsTimeout ? (ushort)1 : (ushort)0);

        // 写入包裹流水号
        BinaryPrimitives.WriteInt32BigEndian(span[2..], PackageId);

        return buffer;
    }

    internal static UploadResultPacket Parse(ushort commandId, ReadOnlySpan<byte> data)
    {
        var isTimeout = BinaryPrimitives.ReadUInt16BigEndian(data) == 1;
        var packageId = BinaryPrimitives.ReadInt32BigEndian(data[2..]);
        return new UploadResultPacket(commandId, isTimeout, packageId);
    }
}

/// <summary>
///     上包结果应答数据包
/// </summary>
internal class UploadResultAckPacket(ushort commandId) : PlcPacket(CommandType.UploadResultAck, commandId)
{
    protected override byte[]? GetMessageBody()
    {
        return null;
    }
}