using Common.Services.Settings;

namespace SortingServices.Servers.Models;

[Configuration("jushuitanSettings")]
public class JushuitanSettings : BindableBase
{
    private string _accessToken = string.Empty;
    private string _appKey = string.Empty;
    private string _appSecret = string.Empty;
    private bool _isProduction;

    /// <summary>
    ///     是否使用正式环境
    /// </summary>
    public bool IsProduction
    {
        get => _isProduction;
        set => SetProperty(ref _isProduction, value);
    }

    /// <summary>
    ///     应用密钥
    /// </summary>
    public string AppKey
    {
        get => _appKey;
        set => SetProperty(ref _appKey, value);
    }

    /// <summary>
    ///     访问令牌
    /// </summary>
    public string AccessToken
    {
        get => _accessToken;
        set => SetProperty(ref _accessToken, value);
    }

    /// <summary>
    ///     应用密钥
    /// </summary>
    public string AppSecret
    {
        get => _appSecret;
        set => SetProperty(ref _appSecret, value);
    }
}