using System.Text.Json.Serialization;
using Common.Services.Settings;

namespace Presentation_KuaiLv.Models.Settings.Warning;

/// <summary>
///     警示灯配置
/// </summary>
[Configuration("WarningLightSettings")]
public class WarningLightConfiguration
{
    /// <summary>
    ///     IP地址
    /// </summary>
    [JsonPropertyName("ipAddress")]
    public string IpAddress { get; set; } = "192.168.0.101";

    /// <summary>
    ///     端口号
    /// </summary>
    [JsonPropertyName("port")]
    public int Port { get; set; } = 2000;

    /// <summary>
    ///     连接超时时间（毫秒）
    /// </summary>
    [JsonPropertyName("connectionTimeout")]
    public int ConnectionTimeout { get; set; } = 3000;

    /// <summary>
    ///     是否启用
    /// </summary>
    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; } = true;
}