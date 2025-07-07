namespace ChileSowing.Models.Api;

/// <summary>
/// 分拣单数据同步响应模型
/// </summary>
public class BatchOrderResponse
{
    /// <summary>
    /// 是否成功（true-正常，false-异常）
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 请求结果代码（SUCCESS表示成功，其他值为错误码）
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// 请求结果描述（描述信息，如错误原因）
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// 时间戳（响应时间，格式如 "2022-03-27 15:06:29"）
    /// </summary>
    public string Time { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>
    /// 扩展项/返回数据
    /// </summary>
    public object? Object { get; set; }

    /// <summary>
    /// 创建成功响应
    /// </summary>
    /// <param name="message">成功消息</param>
    /// <param name="data">返回数据</param>
    /// <returns>成功响应</returns>
    public static BatchOrderResponse CreateSuccess(string? message = null, object? data = null)
    {
        return new BatchOrderResponse
        {
            Success = true,
            Code = "SUCCESS",
            Message = message ?? "操作成功",
            Object = data
        };
    }

    /// <summary>
    /// 创建失败响应
    /// </summary>
    /// <param name="code">错误码</param>
    /// <param name="message">错误消息</param>
    /// <param name="data">返回数据</param>
    /// <returns>失败响应</returns>
    public static BatchOrderResponse CreateFailure(string code, string message, object? data = null)
    {
        return new BatchOrderResponse
        {
            Success = false,
            Code = code,
            Message = message,
            Object = data
        };
    }
} 