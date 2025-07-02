using System.Text;
using System.Text.Json;

namespace XinBeiYang.Models.Communication.JdWcs;

/// <summary>
///     心跳消息体
/// </summary>
public class HeartbeatMessage
{
    // 共享的序列化选项
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    ///     设备编号
    /// </summary>
    public string DeviceNo { get; set; } = string.Empty;

    /// <summary>
    ///     设备状态，0表示停机，1表示运行中，2表示设备故障
    /// </summary>
    public JdDeviceStatus DeviceStatus { get; set; } = JdDeviceStatus.Running;

    /// <summary>
    ///     将消息体序列化为字节数组
    /// </summary>
    public byte[] ToBytes()
    {
        var json = JsonSerializer.Serialize(new
        {
            deviceNo = DeviceNo,
            deviceStatus = (int)DeviceStatus
        }, JsonOptions);
        return Encoding.UTF8.GetBytes(json);
    }

    /// <summary>
    ///     从字节数组解析消息体
    /// </summary>
    public static HeartbeatMessage FromBytes(byte[] bytes)
    {
        var json = Encoding.UTF8.GetString(bytes);
        var jsonDoc = JsonDocument.Parse(json);
        var root = jsonDoc.RootElement;

        return new HeartbeatMessage
        {
            DeviceNo = root.GetProperty("deviceNo").GetString() ?? string.Empty,
            DeviceStatus = (JdDeviceStatus)root.GetProperty("deviceStatus").GetInt32()
        };
    }
}