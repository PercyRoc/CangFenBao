namespace Common.Services.License;

/// <summary>
///     授权服务接口
/// </summary>
public interface ILicenseService
{
    /// <summary>
    ///     验证授权
    /// </summary>
    Task<(bool IsValid, string? Message)> ValidateLicenseAsync();

    /// <summary>
    ///     获取授权过期时间
    /// </summary>
    Task<DateTime> GetExpirationDateAsync();
}