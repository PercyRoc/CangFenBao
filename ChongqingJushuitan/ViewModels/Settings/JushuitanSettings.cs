using Common.Services.Settings;
using Prism.Mvvm;

namespace ChongqingJushuitan.ViewModels.Settings;

[Configuration("jushuitanSettings")]
public class JushuitanSettings : BindableBase
{
    private bool _isProduction;
    private string _appKey = string.Empty;
    private string _appSecret = string.Empty;
    private string _accessToken = string.Empty;
    private string _refreshToken = string.Empty;

    /// <summary>
    /// 是否使用正式环境
    /// </summary>
    public bool IsProduction
    {
        get => _isProduction;
        set => SetProperty(ref _isProduction, value);
    }

    /// <summary>
    /// 应用密钥
    /// </summary>
    public string AppKey
    {
        get => _appKey;
        set => SetProperty(ref _appKey, value);
    }

    /// <summary>
    /// 应用密钥
    /// </summary>
    public string AppSecret
    {
        get => _appSecret;
        set => SetProperty(ref _appSecret, value);
    }

    /// <summary>
    /// 访问令牌
    /// </summary>
    public string AccessToken
    {
        get => _accessToken;
        set => SetProperty(ref _accessToken, value);
    }

    /// <summary>
    /// 刷新令牌
    /// </summary>
    public string RefreshToken
    {
        get => _refreshToken;
        set => SetProperty(ref _refreshToken, value);
    }
} 