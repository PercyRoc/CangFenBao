using XinBa.Services.Models;

namespace XinBa.Services
{
    /// <summary>
    /// Tare Attributes API 服务接口
    /// 用于向 Wildberries API 提交包裹测量属性
    /// </summary>
    public interface ITareAttributesApiService
    {
        /// <summary>
        /// 提交包裹测量属性到 Wildberries API
        /// </summary>
        /// <param name="request">测量属性请求数据</param>
        /// <returns>提交结果：(成功标志, 错误信息)</returns>
        Task<(bool Success, string? ErrorMessage)> SubmitTareAttributesAsync(TareAttributesRequest request);

        /// <summary>
        /// 检查服务是否可用
        /// </summary>
        /// <returns>服务可用性</returns>
        bool IsServiceAvailable();
    }
} 