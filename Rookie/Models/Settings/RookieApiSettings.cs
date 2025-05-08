using Common.Services.Settings;

namespace Rookie.Models.Settings;

/// <summary>
/// Settings related to the Rookie DCS API integration.
/// </summary>
[Configuration("RookieApiSettings")] // This will be saved in Settings/RookieApiSettings.json
public class RookieApiSettings
{
    /// <summary>
    /// 分拣地点编码 (e.g., sorter, pre_sorter)
    /// </summary>
    public string BcrName { get; set; } = "sorter"; // Default value

    /// <summary>
    /// 扫码器/设备编号 (e.g., sorter01)
    /// </summary>
    public string BcrCode { get; set; } = "sorter01"; // Default value

    /// <summary>
    /// DCS API Base URL
    /// </summary>
    public string ApiBaseUrl { get; set; } = "http://localhost:8080"; // Default value
}
