using System.Text.Json;
using System.Text.Json.Serialization;

namespace Presentation_BenFly.Models.BenNiao;

/// <summary>
///     预报数据下载响应
/// </summary>
public class PreReportDataResponse
{
    /// <summary>
    ///     运单号
    /// </summary>
    public string WaybillNum { get; set; } = string.Empty;

    /// <summary>
    ///     三段码
    /// </summary>
    public string SegmentCode { get; set; } = string.Empty;

    /// <summary>
    ///     从数组转换为对象
    /// </summary>
    public static PreReportDataResponse FromArray(JsonElement array)
    {
        if (array.GetArrayLength() != 2) throw new ArgumentException("数组长度必须为2");

        // 获取运单号（支持数字和字符串类型）
        var waybillNum = array[0].ValueKind switch
        {
            JsonValueKind.Number => array[0].GetInt64().ToString(),
            JsonValueKind.String => array[0].GetString() ?? string.Empty,
            _ => string.Empty
        };

        // 获取三段码
        var segmentCode = array[1].GetString() ?? string.Empty;

        return new PreReportDataResponse
        {
            WaybillNum = waybillNum,
            SegmentCode = segmentCode
        };
    }
}

/// <summary>
///     实时查询请求
/// </summary>
public class RealTimeQueryRequest
{
    /// <summary>
    ///     运单编号
    /// </summary>
    [JsonPropertyName("waybillNum")]
    public string WaybillNum { get; set; } = string.Empty;
}

/// <summary>
///     数据上传请求项
/// </summary>
public class DataUploadItem
{
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