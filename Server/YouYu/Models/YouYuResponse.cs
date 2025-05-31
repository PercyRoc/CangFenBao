using System.Text.Json.Serialization;

namespace Server.YouYu.Models
{
    public class YouYuResponse
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("data")]
        public YouYuResponseData? Data { get; set; }
    }

    public class YouYuResponseData
    {
        [JsonPropertyName("boxNo")]
        public string? BoxNo { get; set; }

        [JsonPropertyName("responseTime")]
        public long ResponseTime { get; set; }
    }
} 