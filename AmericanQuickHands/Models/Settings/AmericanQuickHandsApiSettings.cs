namespace AmericanQuickHands.Models.Settings;

/// <summary>
/// Swiftx API设置
/// </summary>
public class AmericanQuickHandsApiSettings
{
    /// <summary>
    /// API基础URL
    /// </summary>
    public string ApiUrl { get; set; } = "https://prod.open.swiftx-express.com";
    
    /// <summary>
    /// 应用密钥
    /// </summary>
    public string AppKey { get; set; } = "404108cbaa0f101452256ac4a83d72be";
    
    /// <summary>
    /// 应用秘钥
    /// </summary>
    public string AppSecret { get; set; } = "a51fc91a621417210a16790766959f615eca8d0ed28e8ac77a78b001af918808";
    
    /// <summary>
    /// 分拣机编码
    /// </summary>
    public string SortingMachineCode { get; set; } = "SORT001";
    
    /// <summary>
    /// 验证设置是否有效
    /// </summary>
    /// <returns></returns>
    public bool IsValid()
    {
        return !string.IsNullOrWhiteSpace(ApiUrl) &&
               !string.IsNullOrWhiteSpace(AppKey) &&
               !string.IsNullOrWhiteSpace(AppSecret) &&
               !string.IsNullOrWhiteSpace(SortingMachineCode);
    }
    
    /// <summary>
    /// 获取完整的API URL
    /// </summary>
    /// <returns></returns>
    public string GetFullApiUrl()
    {
        return ApiUrl.TrimEnd('/');
    }
}