using System.Collections.Generic;

namespace XinBa.Services.Models
{
    /// <summary>
    /// API错误响应模型，匹配OpenAPI文档定义
    /// </summary>
    public class ErrorResponse
    {
        /// <summary>
        /// 错误代码或简短错误信息
        /// </summary>
        public string error { get; set; } = string.Empty;
        
        /// <summary>
        /// 详细错误说明
        /// </summary>
        public string message { get; set; } = string.Empty;
    }
    
    /// <summary>
    /// 兼容旧版本的错误详情模型（如果需要保留）
    /// </summary>
    public class ErrorDetail
    {
        public string error { get; set; } = string.Empty;
        public string message { get; set; } = string.Empty;
        public string detail { get; set; } = string.Empty;
    }

    /// <summary>
    /// 兼容旧版本的错误响应模型（如果需要保留）
    /// </summary>
    public class LegacyErrorResponse
    {
        public List<ErrorDetail> errors { get; set; } = new();
    }
} 