namespace XinBa.Services.Models;

/// <summary>
///     API错误响应
/// </summary>
public class ErrorResponse
{
    /// <summary>
    ///     错误代码
    /// </summary>
    public string Error { get; set; } = string.Empty;

    /// <summary>
    ///     错误消息
    /// </summary>
    public string Message { get; set; } = string.Empty;
}