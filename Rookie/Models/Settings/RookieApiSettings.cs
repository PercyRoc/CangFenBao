using Common.Services.Settings;
using System.Text.Json.Serialization;

namespace Rookie.Models.Settings;

/// <summary>
/// 与菜鸟DCS API集成相关的设置。
/// </summary>
[Configuration("RookieApiSettings")] 
public class RookieApiSettings
{
    /// <summary>
    /// 分拣地点编码 (e.g., sorter, pre_sorter)
    /// </summary>
    public string BcrName { get; set; } = "sorter";

    /// <summary>
    /// 扫码器/设备编号 (e.g., sorter01)
    /// </summary>
    public string BcrCode { get; set; } = "sorter01";

    /// <summary>
    /// DCS API Base URL
    /// </summary>
    [JsonPropertyName("ApiBaseUrl")]
    public string ApiBaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>
    /// API请求的来源标识
    /// </summary>
    [JsonPropertyName("Source")]
    public string Source { get; set; } = "CangFenBaoWcs";

    /// <summary>
    /// 图片上传地址
    /// </summary>
    [JsonPropertyName("ImageUploadUrl")]
    public string ImageUploadUrl { get; set; } = "http://localhost:8080/api/image/upload";
}
