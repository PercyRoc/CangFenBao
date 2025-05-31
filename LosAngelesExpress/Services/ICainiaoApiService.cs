using Common.Models.Package;

namespace LosAngelesExpress.Services;

/// <summary>
/// 洛杉矶菜鸟API服务接口
/// </summary>
public interface ICainiaoApiService
{
    /// <summary>
    /// 上传包裹信息到菜鸟服务器
    /// </summary>
    /// <param name="packageInfo">包裹信息</param>
    /// <returns>上传结果</returns>
    Task<CainiaoApiUploadResult> UploadPackageAsync(PackageInfo packageInfo);

    /// <summary>
    /// 检查服务连接状态
    /// </summary>
    /// <returns>是否连接正常</returns>
    Task<bool> CheckConnectionAsync();
}

/// <summary>
/// 菜鸟API上传结果
/// </summary>
public class CainiaoApiUploadResult
{
    /// <summary>
    /// 操作是否成功
    /// </summary>
    public bool IsSuccess { get; set; }

    /// <summary>
    /// 分拣代码
    /// </summary>
    public string? SortCode { get; set; }

    /// <summary>
    /// 错误消息
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// HTTP状态码
    /// </summary>
    public int? HttpStatusCode { get; set; }

    /// <summary>
    /// 响应时间（毫秒）
    /// </summary>
    public long ResponseTimeMs { get; set; }

    /// <summary>
    /// 创建成功结果
    /// </summary>
    public static CainiaoApiUploadResult Success(string? sortCode, long responseTimeMs) =>
        new()
        {
            IsSuccess = true,
            SortCode = sortCode,
            ResponseTimeMs = responseTimeMs
        };

    /// <summary>
    /// 创建失败结果
    /// </summary>
    public static CainiaoApiUploadResult Failure(string errorMessage, int? httpStatusCode = null, long responseTimeMs = 0) =>
        new()
        {
            IsSuccess = false,
            ErrorMessage = errorMessage,
            HttpStatusCode = httpStatusCode,
            ResponseTimeMs = responseTimeMs
        };
} 