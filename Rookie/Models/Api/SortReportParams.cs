using System.Text.Json.Serialization;

namespace Rookie.Models.Api;

public class SortReportParams
{
    [JsonPropertyName("bcrName")] public string BcrName { get; set; } = string.Empty;

    [JsonPropertyName("barCode")] public string BarCode { get; set; } = string.Empty;

    [JsonPropertyName("chuteCode")] public string ChuteCode { get; set; } = string.Empty;

    [JsonPropertyName("bcrCode")] public string BcrCode { get; set; } = string.Empty;

    [JsonPropertyName("status")] public int Status { get; set; } // 0: success, 1: failed

    [JsonPropertyName("errorReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorReason { get; set; }
}