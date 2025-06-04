using Newtonsoft.Json;

namespace Modules.Models.Jitu
{
    public class JituOpScanRequest
    {
        [JsonProperty("billcode")]
        public string Billcode { get; set; }

        [JsonProperty("weight")]
        public double Weight { get; set; }

        [JsonProperty("length")]
        public double Length { get; set; }

        [JsonProperty("width")]
        public double Width { get; set; }

        [JsonProperty("height")]
        public double Height { get; set; }

        [JsonProperty("devicecode")]
        public string Devicecode { get; set; }

        [JsonProperty("devicename")]
        public string Devicename { get; set; }

        [JsonProperty("imgpath")]
        public string Imgpath { get; set; }
    }
} 