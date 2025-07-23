using System.Buffers.Binary;

// Added for BigEndian support

namespace XinBeiYang.Models.Communication.JdWcs;

/// <summary>
///     京东WCS消息头
/// </summary>
public class JdWcsMessageHeader
{
    /// <summary>
    ///     构造函数
    /// </summary>
    public JdWcsMessageHeader()
    {
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
    /// <summary>
    ///     魔数，固定值20230101
    /// </summary>
    public int MagicNumber { get; set; } = 20230101;

    /// <summary>
    ///     消息类型
    /// </summary>
    public JdWcsMessageType MessageType { get; set; }

    /// <summary>
    ///     消息序号
    /// </summary>
    public int MessageSequence { get; set; }

    /// <summary>
    ///     数据长度
    /// </summary>
    public int DataLength { get; set; }

    /// <summary>
    ///     协议版本号，默认2，支持格口数量上限32767
    /// </summary>
    public sbyte ProtocolVersion { get; set; } = JdWcsConstants.ProtocolVersion;

    /// <summary>
    ///     数据格式，1：JSON
    /// </summary>
    public sbyte DataFormat { get; set; } = 1; // 固定使用JSON格式

    /// <summary>
    ///     厂商序号
    /// </summary>
    public short VendorId { get; set; } // 由WCS分配

    /// <summary>
    ///     设备类型，如1：交叉带分拣机（环形），2：窄带分拣机等
    /// </summary>
    public sbyte DeviceType { get; set; } = 1; // 默认交叉带分拣机

    /// <summary>
    ///     是否回复ACK，0：不需要，1：需要
    /// </summary>
    public sbyte NeedAck { get; set; } = 1; // 默认需要回复

    /// <summary>
    ///     时间戳（13位长度毫秒级）
    /// </summary>
    public long Timestamp { get; set; }

    /// <summary>
    ///     保留信息
    /// </summary>
    public byte[] Reserved { get; set; } = new byte[4];

    /// <summary>
    ///     获取消息头长度（固定32字节）
    /// </summary>
    public static int Size
    {
        get => 32;
    }

    /// <summary>
    ///     将消息头序列化为字节数组 (使用大端字节序)
    /// </summary>
    public byte[] ToBytes()
    {
        var buffer = new byte[Size];
        var span = buffer.AsSpan();

        BinaryPrimitives.WriteInt32BigEndian(span.Slice(0, 4), MagicNumber);
        BinaryPrimitives.WriteInt16BigEndian(span.Slice(4, 2), (short)MessageType);
        BinaryPrimitives.WriteInt32BigEndian(span.Slice(6, 4), MessageSequence);
        BinaryPrimitives.WriteInt32BigEndian(span.Slice(10, 4), DataLength);
        span[14] = (byte)ProtocolVersion;
        span[15] = (byte)DataFormat;
        BinaryPrimitives.WriteInt16BigEndian(span.Slice(16, 2), VendorId);
        span[18] = (byte)DeviceType;
        span[19] = (byte)NeedAck;
        BinaryPrimitives.WriteInt64BigEndian(span.Slice(20, 8), Timestamp);
        Reserved.CopyTo(buffer, 28);

        return buffer;
    }

    /// <summary>
    ///     从字节数组解析消息头 (使用大端字节序)
    /// </summary>
    public static JdWcsMessageHeader FromBytes(byte[] bytes)
    {
        if (bytes.Length < Size)
        {
            throw new ArgumentException(@"Byte array too short to be a valid header.", nameof(bytes));
        }
        var span = bytes.AsSpan();

        var header = new JdWcsMessageHeader
        {
            MagicNumber = BinaryPrimitives.ReadInt32BigEndian(span[..4]),
            MessageType = (JdWcsMessageType)BinaryPrimitives.ReadInt16BigEndian(span.Slice(4, 2)),
            MessageSequence = BinaryPrimitives.ReadInt32BigEndian(span.Slice(6, 4)),
            DataLength = BinaryPrimitives.ReadInt32BigEndian(span.Slice(10, 4)),
            ProtocolVersion = (sbyte)span[14],
            DataFormat = (sbyte)span[15],
            VendorId = BinaryPrimitives.ReadInt16BigEndian(span.Slice(16, 2)),
            DeviceType = (sbyte)span[18],
            NeedAck = (sbyte)span[19],
            Timestamp = BinaryPrimitives.ReadInt64BigEndian(span.Slice(20, 8))
        };
        span.Slice(28, 4).CopyTo(header.Reserved);

        return header;
    }
}