using System.Text.Json.Serialization;

namespace Common.Services.License;

/// <summary>
///     日期限制配置
/// </summary>
public class DateLimitConfig
{
    /// <summary>
    ///     是否检查日期
    /// </summary>
    [JsonPropertyName("check_date")]
    public bool CheckDate { get; init; }

    /// <summary>
    ///     有效期截止日期
    /// </summary>
    [JsonPropertyName("valid_date")]
    public DateTime ValidDate { get; init; }

    /// <summary>
    ///     是否有效
    /// </summary>
    [JsonPropertyName("is_valid")]
    public bool IsValid { get; set; }

    /// <summary>
    ///     失效日期
    /// </summary>
    [JsonPropertyName("invalid_date")]
    public DateTime InvalidDate { get; set; }
}