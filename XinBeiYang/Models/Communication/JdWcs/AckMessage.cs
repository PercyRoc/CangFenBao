using System.Text;
using System.Text.Json;

namespace XinBeiYang.Models.Communication.JdWcs;

/// <summary>
/// 通用ACK确认消息体
/// </summary>
public class AckMessage
{
    // 共享的序列化选项
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    
    /// <summary>
    /// 设备编号
    /// </summary>
    public string DeviceNo { get; set; } = string.Empty;
    
    /// <summary>
    /// 确认码，1表示成功，0表示失败
    /// </summary>
    public int Code { get; set; } = 1;
    
    /// <summary>
    /// 将消息体序列化为JSON格式的字节数组
    /// </summary>
    public byte[] ToBytes()
    {
        var json = JsonSerializer.Serialize(this, _jsonOptions);
        return Encoding.UTF8.GetBytes(json);
    }
    
    /// <summary>
    /// 从JSON格式的字节数组解析消息体
    /// </summary>
    public static AckMessage FromBytes(byte[] bytes)
    {
        var json = Encoding.UTF8.GetString(bytes);
        return JsonSerializer.Deserialize<AckMessage>(json, _jsonOptions) ?? new AckMessage();
    }
} 