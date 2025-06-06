using System.Text.Json.Serialization;

namespace FuzhouPolicyForce.Models
{
    /// <summary>
    /// 申通仓客户出库自动揽收请求（按照官方文档规范）
    /// </summary>
    public class ShenTongLanShouRequest
    {
        /// <summary>
        /// 仓编码（必填）
        /// </summary>
        [JsonPropertyName("whCode")]
        public string WhCode { get; set; } = string.Empty;

        /// <summary>
        /// 揽收网点编码（必填）
        /// </summary>
        [JsonPropertyName("orgCode")]
        public string OrgCode { get; set; } = string.Empty;

        /// <summary>
        /// 揽收员编码（必填）
        /// </summary>
        [JsonPropertyName("userCode")]
        public string UserCode { get; set; } = string.Empty;

        /// <summary>
        /// 包裹列表（必填）
        /// </summary>
        [JsonPropertyName("packages")]
        public List<ShenTongPackageDto> Packages { get; set; } = [];
    }

    /// <summary>
    /// 申通包裹信息（简化版，按照文档要求）
    /// </summary>
    public class ShenTongPackageDto
    {
        /// <summary>
        /// 运单号（必填）
        /// </summary>
        [JsonPropertyName("waybillNo")]
        public string WaybillNo { get; set; } = string.Empty;

        /// <summary>
        /// 重量，单位kg，精确2位小数（必填）
        /// </summary>
        [JsonPropertyName("weight")]
        public string Weight { get; set; } = string.Empty;

        /// <summary>
        /// 揽收时间（必填）
        /// 格式：yyyy-MM-dd HH:mm:ss
        /// </summary>
        [JsonPropertyName("opTime")]
        public string OpTime { get; set; } = string.Empty;
    }
} 