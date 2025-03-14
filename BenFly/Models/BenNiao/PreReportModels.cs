using System.Text.Json.Serialization;

namespace BenFly.Models.BenNiao;

/// <summary>
///     预报数据下载响应
/// </summary>
public class PreReportDataResponse
{
    /// <summary>
    ///     运单号
    /// </summary>
    [JsonPropertyName("waybillNum")]
    internal string WaybillNum { get; private set; } = string.Empty;

    /// <summary>
    ///     三段码
    /// </summary>
    [JsonPropertyName("segmentCode")]
    internal string SegmentCode { get; private set; } = string.Empty;
}

/// <summary>
///     数据上传请求项
/// </summary>
internal class DataUploadItem
{
    /// <summary>
    ///     设备扫描分拨中心名称
    /// </summary>
    [JsonPropertyName("netWorkName")]
    public string NetworkName { get; set; } = string.Empty;

    /// <summary>
    ///     运单号
    /// </summary>
    [JsonPropertyName("waybillNum")]
    public string WaybillNum { get; set; } = string.Empty;

    /// <summary>
    ///     扫描时间，yyyy-MM-dd HH:mm:ss
    /// </summary>
    [JsonPropertyName("scanTime")]
    public string ScanTime { get; set; } = string.Empty;

    /// <summary>
    ///     重量，实际重量，单位：千克，2位小数
    /// </summary>
    [JsonPropertyName("weight")]
    public decimal Weight { get; set; }

    /// <summary>
    ///     长，单位：厘米
    /// </summary>
    [JsonPropertyName("goodsLength")]
    public int GoodsLength { get; set; }

    /// <summary>
    ///     宽，单位：厘米
    /// </summary>
    [JsonPropertyName("goodsWidth")]
    public int GoodsWidth { get; set; }

    /// <summary>
    ///     高，单位：厘米
    /// </summary>
    [JsonPropertyName("goodsHeight")]
    public int GoodsHeight { get; set; }

    /// <summary>
    ///     设备号
    /// </summary>
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;
}