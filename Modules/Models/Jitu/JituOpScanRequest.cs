using Newtonsoft.Json;

namespace ShanghaiModuleBelt.Models.Jitu;

public class JituOpScanRequest
{
    [JsonProperty("billcode")]
    public string Billcode { get; set; } = string.Empty;

    [JsonProperty("weight")]
    public double Weight { get; set; }

    [JsonProperty("length")]
    public double Length { get; set; }

    [JsonProperty("width")]
    public double Width { get; set; }

    [JsonProperty("height")]
    public double Height { get; set; }

    [JsonProperty("devicecode")]
    public string Devicecode { get; set; } = string.Empty;

    [JsonProperty("devicename")]
    public string Devicename { get; set; } = string.Empty;

    [JsonProperty("imgpath")]
    public string Imgpath { get; set; } = string.Empty;
}