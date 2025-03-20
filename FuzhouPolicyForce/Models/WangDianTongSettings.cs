using System.ComponentModel.DataAnnotations;
using Common.Services.Settings;
using Prism.Mvvm;

namespace FuzhouPolicyForce.Models;

/// <summary>
/// 旺店通配置
/// </summary>
[Configuration("WangDianTong")]
public class WangDianTongSettings : BindableBase
{
    private bool _isProductionEnvironment;
    private string _sellerAccount = string.Empty;
    private string _apiAccount = string.Empty;
    private string _apiSecret = string.Empty;
    
    /// <summary>
    /// 是否生产环境
    /// </summary>
    public bool IsProductionEnvironment
    {
        get => _isProductionEnvironment;
        set => SetProperty(ref _isProductionEnvironment, value);
    }
    
    /// <summary>
    /// 卖家账号
    /// </summary>
    [Required(ErrorMessage = "卖家账号不能为空")]
    public string SellerAccount
    {
        get => _sellerAccount;
        set => SetProperty(ref _sellerAccount, value);
    }
    
    /// <summary>
    /// API账号
    /// </summary>
    [Required(ErrorMessage = "API账号不能为空")]
    public string ApiAccount
    {
        get => _apiAccount;
        set => SetProperty(ref _apiAccount, value);
    }
    
    /// <summary>
    /// API密钥
    /// </summary>
    [Required(ErrorMessage = "API密钥不能为空")]
    public string ApiSecret
    {
        get => _apiSecret;
        set => SetProperty(ref _apiSecret, value);
    }
    
    /// <summary>
    /// 获取API基础URL
    /// </summary>
    public string GetApiBaseUrl()
    {
        return IsProductionEnvironment 
            ? "https://api.wangdian.cn/openapi2/" 
            : "https://sandbox.wangdian.cn/openapi2/";
    }
} 