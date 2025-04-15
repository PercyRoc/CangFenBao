using System.Text;
using System.Text.Json;

namespace XinBeiYang.Models.Communication.JdWcs;

/// <summary>
/// 扫描包裹图片地址上报消息体
/// </summary>
public class ImageUrlReportMessage
{
    // 共享的序列化选项
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    
    /// <summary>
    /// 设备编号，规则为"P（扫描仪标识符）-主设备号-扫描仪序号"
    /// </summary>
    public string DeviceNo { get; set; } = string.Empty;
    
    /// <summary>
    /// 包裹扫描流水号，需与回传主控PLC扫码结果任务号保持一致
    /// </summary>
    public int TaskNo { get; set; }
    
    /// <summary>
    /// 一维条码字符信息，未识别到条码时上报noread
    /// </summary>
    public List<string> Barcode { get; set; } = [];
    
    /// <summary>
    /// 二维码字符信息，未识别到条码时上报noread
    /// </summary>
    public List<string> MatrixBarcode { get; set; } = [];
    
    /// <summary>
    /// 包裹图片地址信息，多个地址按照英文半角逗号分隔
    /// </summary>
    public string ImageUrl { get; set; } = string.Empty;
    
    /// <summary>
    /// 主控PLC触发扫码生成的时间戳信息（13位毫秒精度）
    /// </summary>
    public long Timestamp { get; set; }
    
    /// <summary>
    /// 一次扫描回传给京东的图片地址总数量，取值范围1~127
    /// </summary>
    public sbyte PicQty { get; set; } = 1;
    
    /// <summary>
    /// 一次扫描回传图片地址总报文数量，取值范围1~127
    /// </summary>
    public sbyte MsgQty { get; set; } = 1;
    
    /// <summary>
    /// 当前报文序号：1~报文总数
    /// </summary>
    public sbyte MsgSeq { get; set; } = 1;
    
    /// <summary>
    /// 将消息体序列化为字节数组
    /// </summary>
    public byte[] ToBytes()
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        return Encoding.UTF8.GetBytes(json);
    }
    
    /// <summary>
    /// 从字节数组解析消息体
    /// </summary>
    public static ImageUrlReportMessage FromBytes(byte[] bytes)
    {
        var json = Encoding.UTF8.GetString(bytes);
        return JsonSerializer.Deserialize<ImageUrlReportMessage>(json, JsonOptions) ?? new ImageUrlReportMessage();
    }
} 