using System.Text.Json.Serialization;

namespace SowingWall.Models.WangDianTong.Api
{
    /// <summary>
    /// 旺店通 API 响应基类
    /// </summary>
    public abstract class WdtApiResponseBase
    {
        /// <summary>
        /// 返回结果，success / failure
        /// </summary>
        [JsonPropertyName("flag")]
        public string Flag { get; set; } = string.Empty;

        /// <summary>
        /// 错误码
        /// </summary>
        [JsonPropertyName("code")]
        public string Code { get; set; } = string.Empty;

        /// <summary>
        /// 错误信息
        /// </summary>
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// 是否成功
        /// </summary>
        [JsonIgnore] // 不参与序列化
        public bool IsSuccess => Flag?.Equals("success", StringComparison.OrdinalIgnoreCase) ?? false;
    }
} 