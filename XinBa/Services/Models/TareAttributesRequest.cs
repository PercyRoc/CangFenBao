using System.Text.Json.Serialization;

namespace XinBa.Services.Models
{
    public class TareAttributesRequest
    {
        [JsonPropertyName("office_id")]
        public long OfficeId { get; set; }

        [JsonPropertyName("tare_sticker")]
        public string TareSticker { get; set; }

        [JsonPropertyName("place_id")]
        public long PlaceId { get; set; }

        [JsonPropertyName("size_a_mm")]
        public long SizeAMm { get; set; }

        [JsonPropertyName("size_b_mm")]
        public long SizeBMm { get; set; }

        [JsonPropertyName("size_c_mm")]
        public long SizeCMm { get; set; }

        [JsonPropertyName("volume_mm")]
        public int VolumeMm { get; set; }

        [JsonPropertyName("weight_g")]
        public int WeightG { get; set; }
    }
} 