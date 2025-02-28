using System.Buffers.Binary;
using System.Text;

namespace Presentation_XinBeiYang.Models.Communication.Packets;

/// <summary>
///     上包请求数据包
/// </summary>
public class UploadRequestPacket(
    ushort commandId,
    float weight,
    float length,
    float width,
    float height,
    string barcode1D,
    string barcode2D,
    ulong scanTimestamp)
    : PlcPacket(CommandType.UploadRequest, commandId)
{
    /// <summary>
    ///     重量（kg）
    /// </summary>
    public float Weight { get; } = weight;

    /// <summary>
    ///     长度（mm）
    /// </summary>
    public float Length { get; } = length;

    /// <summary>
    ///     宽度（mm）
    /// </summary>
    public float Width { get; } = width;

    /// <summary>
    ///     高度（mm）
    /// </summary>
    public float Height { get; } = height;

    /// <summary>
    ///     一维码
    /// </summary>
    public string Barcode1D { get; } = barcode1D;

    /// <summary>
    ///     二维码
    /// </summary>
    public string Barcode2D { get; } = barcode2D;

    /// <summary>
    ///     扫描时间戳
    /// </summary>
    public ulong ScanTimestamp { get; } = scanTimestamp;

    protected override byte[] GetMessageBody()
    {
        // 计算一维码和二维码的字节数组
        var barcode1DBytes = Encoding.ASCII.GetBytes(Barcode1D);
        var barcode2DBytes = Encoding.ASCII.GetBytes(Barcode2D);

        // 计算消息体总长度
        var totalLength = 4 * 4 + // 4个float
                          4 + barcode1DBytes.Length + // 一维码长度字段和内容
                          4 + barcode2DBytes.Length + // 二维码长度字段和内容
                          8; // 时间戳

        var buffer = new byte[totalLength];
        var span = buffer.AsSpan();
        var position = 0;

        // 写入重量、长度、宽度、高度
        BinaryPrimitives.WriteSingleBigEndian(span[position..], Weight);
        position += 4;
        BinaryPrimitives.WriteSingleBigEndian(span[position..], Length);
        position += 4;
        BinaryPrimitives.WriteSingleBigEndian(span[position..], Width);
        position += 4;
        BinaryPrimitives.WriteSingleBigEndian(span[position..], Height);
        position += 4;

        // 写入一维码
        BinaryPrimitives.WriteInt32BigEndian(span[position..], barcode1DBytes.Length);
        position += 4;
        barcode1DBytes.CopyTo(span[position..]);
        position += barcode1DBytes.Length;

        // 写入二维码
        BinaryPrimitives.WriteInt32BigEndian(span[position..], barcode2DBytes.Length);
        position += 4;
        barcode2DBytes.CopyTo(span[position..]);
        position += barcode2DBytes.Length;

        // 写入时间戳
        BinaryPrimitives.WriteUInt64BigEndian(span[position..], ScanTimestamp);

        return buffer;
    }
}

/// <summary>
///     上包请求应答数据包
/// </summary>
public class UploadRequestAckPacket : PlcPacket
{
    private UploadRequestAckPacket(ushort commandId, bool isAccepted)
        : base(CommandType.UploadRequestAck, commandId)
    {
        IsAccepted = isAccepted;
    }

    /// <summary>
    ///     结果反馈
    /// </summary>
    public bool IsAccepted { get; }

    protected override byte[] GetMessageBody()
    {
        return [0x00, IsAccepted ? (byte)0x00 : (byte)0x01];
    }

    public static UploadRequestAckPacket Parse(ushort commandId, ReadOnlySpan<byte> data)
    {
        var isAccepted = data[1] == 0x00;
        return new UploadRequestAckPacket(commandId, isAccepted);
    }
}