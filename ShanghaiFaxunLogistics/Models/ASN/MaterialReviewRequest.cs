using System.Text.Json.Serialization;

namespace ShanghaiFaxunLogistics.Models.ASN
{
    /// <summary>
    /// 扫码复核请求模型
    /// </summary>
    public class MaterialReviewRequest
    {
        /// <summary>
        /// 系统编码
        /// </summary>
        [JsonPropertyName("systemCode")]
        public string SystemCode { get; set; } = string.Empty;

        /// <summary>
        /// 仓库编码
        /// </summary>
        [JsonPropertyName("houseCode")]
        public string HouseCode { get; set; } = string.Empty;

        /// <summary>
        /// 箱号
        /// </summary>
        [JsonPropertyName("boxCode")]
        public string BoxCode { get; set; } = string.Empty;

        /// <summary>
        /// 月台
        /// </summary>
        [JsonPropertyName("exitArea")]
        public string ExitArea { get; set; } = string.Empty;

        /// <summary>
        /// 扩展项
        /// </summary>
        [JsonPropertyName("extra")]
        public Dictionary<string, object>? Extra { get; set; }
    }
} 