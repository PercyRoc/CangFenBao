using System.Text.Json.Serialization;
using Common.Models.Settings;
using Common.Services.Settings;

namespace Presentation_SeedingWall.Models;

/// <summary>
///     聚水潭设置
/// </summary>
[Configuration("JuShuiTanSettings")]
public class JuShuiTanSettings
{
    /// <summary>
    ///     服务器地址
    /// </summary>
    [JsonPropertyName("serverUrl")]
    public string ServerUrl { get; set; } = "ws://localhost:8080";
}