namespace LosAngelesExpress.Models.Api;
using System.Text.Json.Serialization;

/// <summary>
/// logistics_interface 业务参数对象
/// </summary>
public class CainiaoOpenLogisticsInterface
{
    [JsonPropertyName("bizCode")]
    public string BizCode { get; set; } = string.Empty;

    [JsonPropertyName("weight")]
    public int Weight { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("length")]
    public int Length { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("warehouseCode")]
    public string WarehouseCode { get; set; } = "TRAN_STORE_30867964";

    [JsonPropertyName("workbench")]
    public string Workbench { get; set; } = string.Empty;

    [JsonPropertyName("weightUnit")]
    public string WeightUnit { get; set; } = "g";

    [JsonPropertyName("dimensionUnit")]
    public string DimensionUnit { get; set; } = "cm";
} 