using System.Text.Json.Serialization;

namespace XinBa.Services.Models
{
    /// <summary>
    /// Tare Attributes API 错误详情
    /// </summary>
    public class TareAttributesErrorDetail
    {
        /// <summary>
        /// 错误的机器可读代码
        /// </summary>
        [JsonPropertyName("error")]
        public string Error { get; set; } = string.Empty;

        /// <summary>
        /// 人类可读的错误信息摘要
        /// </summary>
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// 详细的错误描述
        /// </summary>
        [JsonPropertyName("detail")]
        public string Detail { get; set; } = string.Empty;
    }

    /// <summary>
    /// Tare Attributes API 错误响应模型
    /// </summary>
    public class TareAttributesErrorResponse
    {
        /// <summary>
        /// 错误列表
        /// </summary>
        [JsonPropertyName("errors")]
        public List<TareAttributesErrorDetail> Errors { get; set; } = new();
    }
} 