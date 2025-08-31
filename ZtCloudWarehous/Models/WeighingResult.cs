namespace ZtCloudWarehous.Models;

/// <summary>
///     统一称重结果对象
/// </summary>
public class WeighingResult
{
    /// <summary>
    ///     是否成功
    /// </summary>
    public bool IsSuccess { get; set; }

    /// <summary>
    ///     错误码
    /// </summary>
    public string? ErrorCode { get; set; }

    /// <summary>
    ///     错误信息
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    ///     成功的静态实例
    /// </summary>
    public static WeighingResult Success => new() { IsSuccess = true };

    /// <summary>
    ///     创建失败实例的静态方法
    /// </summary>
    public static WeighingResult Fail(string? code, string? message)
    {
        return new WeighingResult
        {
            IsSuccess = false,
            ErrorCode = code,
            ErrorMessage = message
        };
    }
}