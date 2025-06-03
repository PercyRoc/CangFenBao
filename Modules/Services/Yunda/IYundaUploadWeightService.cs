using ShanghaiModuleBelt.Models.Yunda;

namespace ShanghaiModuleBelt.Services.Yunda;

/// <summary>
/// 韵达上传重量接口服务接口
/// </summary>
public interface IYundaUploadWeightService
{
    /// <summary>
    /// 发送上传重量请求
    /// </summary>
    /// <param name="request">韵达上传重量请求</param>
    /// <returns>韵达上传重量响应</returns>
    Task<YundaUploadWeightResponse?> SendUploadWeightRequestAsync(YundaUploadWeightRequest request);
} 