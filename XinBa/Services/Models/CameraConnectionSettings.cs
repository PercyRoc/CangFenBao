using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Common.Models.Settings;
using Common.Services.Settings;

namespace Presentation_XinBa.Services.Models;

/// <summary>
///     相机连接配置
/// </summary>
[Configuration("CameraConnection")]
public class CameraConnectionSettings
{
    /// <summary>
    ///     相机服务器IP地址
    /// </summary>
    [Required]
    [JsonPropertyName("ServerIp")]
    public string? ServerIp { get; set; } = "127.0.0.1";

    /// <summary>
    ///     相机服务器端口
    /// </summary>
    [Required]
    [Range(1, 65535)]
    [JsonPropertyName("ServerPort")]
    public int ServerPort { get; set; } = 8888;

    /// <summary>
    ///     重连间隔（毫秒）
    /// </summary>
    [Range(1000, 60000)]
    [JsonPropertyName("ReconnectIntervalMs")]
    public int ReconnectIntervalMs { get; set; } = 5000;

    /// <summary>
    ///     连接超时（毫秒）
    /// </summary>
    [Range(1000, 60000)]
    [JsonPropertyName("ConnectionTimeoutMs")]
    public int ConnectionTimeoutMs { get; set; } = 10000;

    /// <summary>
    ///     图像保存路径
    /// </summary>
    [Required]
    [JsonPropertyName("ImageSavePath")]
    public string? ImageSavePath { get; set; } = "Images";
}