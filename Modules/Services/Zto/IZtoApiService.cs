using ShanghaiModuleBelt.Models.Zto;

namespace ShanghaiModuleBelt.Services.Zto;

public interface IZtoApiService
{
    /// <summary>
    ///     上传运单轨迹揽收数据
    /// </summary>
    /// <param name="request">揽收上传请求</param>
    /// <returns>揽收上传响应</returns>
    Task<CollectUploadResponse> UploadCollectTraceAsync(CollectUploadRequest request);
}