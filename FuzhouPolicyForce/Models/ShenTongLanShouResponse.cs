using System.Text.Json.Serialization;

namespace FuzhouPolicyForce.Models
{
    public class ShenTongLanShouResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("errorMsg")]
        public string? ErrorMsg { get; set; }

        [JsonPropertyName("errorCode")]
        public string? ErrorCode { get; set; }

        [JsonPropertyName("data")]
        public string? Data { get; set; }
    }
} 