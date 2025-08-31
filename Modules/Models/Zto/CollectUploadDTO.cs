using Newtonsoft.Json;

namespace ShanghaiModuleBelt.Models.Zto;

public class CollectUploadDto
{
    /// <summary>
    ///     运单号
    /// </summary>
    [JsonProperty("billCode")]
    public string BillCode { get; set; } = string.Empty;

    /// <summary>
    ///     重量，单位：kg
    /// </summary>
    [JsonProperty("weight")]
    public decimal Weight { get; set; }
}