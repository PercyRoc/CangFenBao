using System.Text.Json.Serialization;

namespace KuaiLv.Models.DWS;

/// <summary>
///     DWS请求模型
/// </summary>
internal class DwsRequest
{
    /// <summary>
    ///     包裹码（若无法解析二维码则赋值为noread）
    /// </summary>
    [JsonPropertyName("barCode")]
    public string BarCode { get; set; } = "noread";

    /// <summary>
    ///     重量（kg，两位小数）
    /// </summary>
    [JsonPropertyName("weight")]
    public double Weight { get; set; }

    /// <summary>
    ///     长（cm，一位小数）
    /// </summary>
    [JsonPropertyName("length")]
    public double Length { get; set; }

    /// <summary>
    ///     宽（cm，一位小数）
    /// </summary>
    [JsonPropertyName("width")]
    public double Width { get; set; }

    /// <summary>
    ///     高（cm，一位小数）
    /// </summary>
    [JsonPropertyName("height")]
    public double Height { get; set; }

    /// <summary>
    ///     体积（cm³，一位小数）
    /// </summary>
    [JsonPropertyName("volume")]
    public double Volume { get; set; }

    /// <summary>
    ///     场景描述：1称重，2收货，3称重+收货
    /// </summary>
    [JsonPropertyName("operateScene")]
    public int OperateScene { get; set; } = 1;

    /// <summary>
    ///     采集时间（格式：yyyy-MM-dd HH:mm:ss）
    /// </summary>
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    /// <summary>
    ///     照片Base64编码字符串
    /// </summary>
    [JsonPropertyName("image")]
    public string? Image { get; set; }

    /// <summary>
    ///     照片名称
    /// </summary>
    [JsonPropertyName("imagename")]
    public string? ImageName { get; set; }
}