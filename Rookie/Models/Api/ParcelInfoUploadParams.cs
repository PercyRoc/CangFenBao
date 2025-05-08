using System.Text.Json.Serialization;

namespace Rookie.Models.Api;

public class ParcelInfoUploadParams
{
    [JsonPropertyName("bcrName")]
    public string BcrName { get; set; } = string.Empty;

    [JsonPropertyName("barCode")]
    public string BarCode { get; set; } = string.Empty;

    [JsonPropertyName("bcrCode")]
    public string BcrCode { get; set; } = string.Empty;

    [JsonPropertyName("weight")]
    public long Weight { get; set; } // API expects grams (g)

    [JsonPropertyName("height")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Height { get; set; } // API expects millimeters (mm)

    [JsonPropertyName("width")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Width { get; set; } // API expects millimeters (mm)

    [JsonPropertyName("length")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Length { get; set; } // API expects millimeters (mm)

    [JsonPropertyName("volume")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Volume { get; set; } // API expects cubic millimeters (mm³)

    [JsonPropertyName("boxType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BoxType { get; set; }

    [JsonPropertyName("pictureOssPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PictureOssPath { get; set; }
} 