using System.Text.Json.Serialization;

namespace FuzhouPolicyForce.WangDianTong;

/// <summary>
/// 旺店通重量回传响应结果V2 (符合新文档)
/// </summary>
public class WeightPushResponseV2
{
    /// <summary>
    /// 错误码，"0"表示成功，其他表示失败 (根据现有Response结构推测包含)
    /// </summary>
    public string? Code { get; init; }

    /// <summary>
    /// 错误描述 (根据现有Response结构推测包含)
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// 出口通道号。
    /// </summary>
    [JsonPropertyName("export_num")]
    public string? ExportNum { get; init; }

    /// <summary>
    /// 收件人省，比如：北京
    /// </summary>
    [JsonPropertyName("receiver_province")]
    public string? ReceiverProvince { get; init; }

    /// <summary>
    /// 收件人市，比如：北京市
    /// </summary>
    [JsonPropertyName("receiver_city")]
    public string? ReceiverCity { get; init; }

    /// <summary>
    /// 收件人区，比如：朝阳区
    /// </summary>
    [JsonPropertyName("receiver_district")]
    public string? ReceiverDistrict { get; init; }

    /// <summary>
    /// 是否成功 (辅助属性，根据Code判断)
    /// </summary>
    public bool IsSuccess => Code == "0";

    /// <summary>
    /// Service层判断的最终成功状态（综合考虑Code和Message是否为空等因素）
    /// </summary>
    public bool ServiceSuccess { get; set; }
} 