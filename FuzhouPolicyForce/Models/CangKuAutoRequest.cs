using System.Text.Json.Serialization;

namespace FuzhouPolicyForce.Models
{
    public class CangKuAutoRequest
    {
        [JsonPropertyName("whCode")] public string? WhCode { get; set; }
        [JsonPropertyName("orgCode")] public string? OrgCode { get; set; }
        [JsonPropertyName("userCode")] public string? UserCode { get; set; }
        [JsonPropertyName("packages")] public List<CangKuAutoPackageDto>? Packages { get; set; }
    }

    public class CangKuAutoPackageDto
    {
        [JsonPropertyName("waybillNo")] public string? WaybillNo { get; set; }
        [JsonPropertyName("weight")] public string? Weight { get; set; }
        [JsonPropertyName("opTime")] public string? OpTime { get; set; }
    }
}
