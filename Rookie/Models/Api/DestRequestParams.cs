using System.Text.Json.Serialization;

namespace Rookie.Models.Api;

public class DestRequestParams
{
    [JsonPropertyName("bcrName")] public string BcrName { get; set; } = string.Empty;

    [JsonPropertyName("barCode")] public string BarCode { get; set; } = string.Empty;

    [JsonPropertyName("bcrCode")] public string BcrCode { get; set; } = string.Empty;

    // Although doc says required, often this might not be available. Defaulting to "NoRead".
    [JsonPropertyName("itemBarcode")] public string ItemBarcode { get; set; } = "NoRead";
}