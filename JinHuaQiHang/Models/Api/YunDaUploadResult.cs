using Newtonsoft.Json;

namespace JinHuaQiHang.Models.Api
{
    public class YunDaUploadResult
    {
        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("msg")]
        public string Message { get; set; }

        [JsonProperty("id")]
        public string Id { get; set; }
    }
} 