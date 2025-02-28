using System.Buffers.Binary;
using Presentation_XinBeiYang.Models.Communication.Packets;

namespace Presentation_XinBeiYang.Models.Communication;

/// <summary>
/// PLC通讯数据包基类
/// </summary>
public abstract class PlcPacket(CommandType commandType, ushort commandId)
{
    /// <summary>
    /// 指令类型
    /// </summary>
    private CommandType CommandType { get; } = commandType;

    /// <summary>
    /// 指令ID
    /// </summary>
    public ushort CommandId { get; } = commandId;

    /// <summary>
    /// 获取数据包的字节数组
    /// </summary>
    public byte[] ToBytes()
    {
        // 获取消息体
        var messageBody = GetMessageBody();
        
        // 计算总长度：报文头(2) + 长度(2) + 功能码(2) + 指令ID(2) + 消息体 + 报文尾(2)
        var totalLength = 10 + (messageBody?.Length ?? 0);
        
        // 创建字节数组
        var buffer = new byte[totalLength];
        var span = buffer.AsSpan();
        
        // 写入报文头
        span[0] = PlcConstants.StartHeader1;
        span[1] = PlcConstants.StartHeader2;
        
        // 写入长度
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)totalLength);
        
        // 写入功能码
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], (ushort)CommandType);
        
        // 写入指令ID
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], CommandId);
        
        // 写入消息体
        if (messageBody != null)
        {
            messageBody.CopyTo(span[8..]);
        }
        
        // 写入报文尾
        span[^2] = PlcConstants.EndTail1;
        span[^1] = PlcConstants.EndTail2;
        
        return buffer;
    }

    /// <summary>
    /// 获取消息体字节数组
    /// </summary>
    protected abstract byte[]? GetMessageBody();

    /// <summary>
    /// 解析数据包
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out PlcPacket? packet)
    {
        packet = null;
        
        // 检查最小长度
        if (data.Length < 10)
            return false;
        
        // 检查报文头和报文尾
        if (data[0] != PlcConstants.StartHeader1 || data[1] != PlcConstants.StartHeader2 ||
            data[^2] != PlcConstants.EndTail1 || data[^1] != PlcConstants.EndTail2)
            return false;
        
        // 获取长度
        var length = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        if (length != data.Length)
            return false;
        
        // 获取功能码和指令ID
        var commandType = (CommandType)BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        var commandId = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
        
        // 获取消息体
        var messageBody = data[8..^2];
        
        // 根据功能码创建具体的数据包
        packet = CreatePacket(commandType, commandId, messageBody);
        return packet != null;
    }

    /// <summary>
    /// 根据功能码创建具体的数据包
    /// </summary>
    private static PlcPacket? CreatePacket(CommandType commandType, ushort commandId, ReadOnlySpan<byte> messageBody)
    {
        return commandType switch
        {
            CommandType.HeartbeatAck => new HeartbeatAckPacket(commandId),
            CommandType.UploadRequestAck => UploadRequestAckPacket.Parse(commandId, messageBody),
            CommandType.UploadResult => UploadResultPacket.Parse(commandId, messageBody),
            CommandType.DeviceStatus => DeviceStatusPacket.Parse(commandId, messageBody),
            _ => null
        };
    }
} 