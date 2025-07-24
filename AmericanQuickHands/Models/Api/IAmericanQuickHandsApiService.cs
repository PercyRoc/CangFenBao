namespace AmericanQuickHands.Models.Api;

/// <summary>
/// Swiftx API服务接口
/// </summary>
public interface IAmericanQuickHandsApiService
{
    /// <summary>
    /// 分拣机扫码接口
    /// </summary>
    /// <param name="request">分拣机扫码请求</param>
    /// <returns>扫码结果</returns>
    Task<ApiResponse<SwiftxResult>> SortingMachineScanAsync(SortingMachineScanRequest request);
    
    /// <summary>
    /// 测试API连接
    /// </summary>
    /// <returns>测试结果</returns>
    Task<ApiResponse<object>> TestConnectionAsync();
}

/// <summary>
/// API响应通用类
/// </summary>
/// <typeparam name="T">响应数据类型</typeparam>
public class ApiResponse<T>
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public T? Data { get; set; }

    /// <summary>
    /// 创建失败响应
    /// </summary>
    /// <param name="message">错误消息</param>
    /// <returns></returns>
    public static ApiResponse<T> CreateFailure(string message)
    {
        return new ApiResponse<T>
        {
            Success = false,
            Message = message
        };
    }
}

/// <summary>
/// Swiftx API标准响应
/// </summary>
public class SwiftxResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// 分拣机扫码请求
/// </summary>
public class SortingMachineScanRequest
{
    /// <summary>
    /// 分拣机编码
    /// </summary>
    public string SortingMachineCode { get; set; } = string.Empty;

    /// <summary>
    /// 重量(kg)
    /// </summary>
    public double WeightKg { get; set; }

    /// <summary>
    /// 长度(cm)
    /// </summary>
    public double LengthCm { get; set; }

    /// <summary>
    /// 高度(cm)
    /// </summary>
    public double HeightCm { get; set; }

    /// <summary>
    /// 宽度(cm)
    /// </summary>
    public double WidthCm { get; set; }

    /// <summary>
    /// 订单号
    /// </summary>
    public string TrackingNumber { get; set; } = string.Empty;
}