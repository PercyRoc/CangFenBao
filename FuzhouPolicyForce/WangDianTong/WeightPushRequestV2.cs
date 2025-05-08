namespace FuzhouPolicyForce.WangDianTong;
using System.Text.Json.Serialization;

/// <summary>
/// 旺店通重量回传请求参数V2 (符合新文档)
/// </summary>
public class WeightPushRequestV2
{
    /// <summary>
    /// 仓储单号。仓储单号、物流单号二选一必填。
    /// </summary>
    [JsonPropertyName("src_order_no")]
    public string? SrcOrderNo { get; set; }

    /// <summary>
    /// 物流单号。仓储单号、物流单号二选一必填。
    /// </summary>
    [JsonPropertyName("logistics_no")]
    public string? LogisticsNo { get; set; }

    /// <summary>
    /// 实际重量，单位kg。
    /// </summary>
    public decimal Weight { get; set; }

    /// <summary>
    /// 体积，单位cm³。若体积为0，则不更新WMS内的体积
    /// </summary>
    public decimal? Volume { get; set; }

    /// <summary>
    /// 长，单位cm。
    /// </summary>
    public decimal? Length { get; set; }

    /// <summary>
    /// 宽，单位cm。
    /// </summary>
    public decimal? Width { get; set; }

    /// <summary>
    /// 高，单位cm。
    /// </summary>
    public decimal? Height { get; set; }

    /// <summary>
    /// 是否执行称重操作。Y : 是, N : 否。不传字段则默认是（Y）。
    /// </summary>
    [JsonPropertyName("is_weight")]
    public string? IsWeight { get; set; } // "Y" or "N"

    /// <summary>
    /// 图片 url，最长 1024 字符
    /// </summary>
    [JsonPropertyName("img_url")]
    public string? ImgUrl { get; set; }

    /// <summary>
    /// 是否用接口传入包装更新已发货单据包装。Y : 是, N : 否。不传字段则默认是（N）。
    /// </summary>
    [JsonPropertyName("update_package")]
    public string? UpdatePackage { get; set; } // "Y" or "N"

    /// <summary>
    /// 称重员的员工编号
    /// </summary>
    [JsonPropertyName("weighter_no")]
    public string? WeighterNo { get; set; }

    /// <summary>
    /// 打包员的员工编号
    /// </summary>
    [JsonPropertyName("packager_no")]
    public string? PackagerNo { get; set; }

    /// <summary>
    /// 包装物条码。该条码在WMS内的所属货品的货品类别需要是包装物。
    /// </summary>
    [JsonPropertyName("package_barcode")]
    public string? PackageBarcode { get; set; }
} 