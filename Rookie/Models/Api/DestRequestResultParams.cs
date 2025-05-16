using System.Text.Json.Serialization;

namespace Rookie.Models.Api;

// Represents the 'params' object within the CommandResult for a dest_request
public class DestRequestResultParams
{
    [JsonPropertyName("bcrName")]
    public string BcrName { get; set; } = string.Empty;

    [JsonPropertyName("barCode")]
    public string BarCode { get; set; } = string.Empty;

    [JsonPropertyName("itemBarcode")]
    public string? ItemBarcode { get; set; }

    [JsonPropertyName("finalBarcode")]
    public string FinalBarcode { get; set; } = string.Empty;

    [JsonPropertyName("chuteCode")]
    public string ChuteCode { get; set; } = string.Empty;

    [JsonPropertyName("errorCode")]
    public int ErrorCode { get; set; } // 0: Normal, 1: No Rule, 2: No Task, 3: 称重模块 Ex, 4: Biz Intercept
} 