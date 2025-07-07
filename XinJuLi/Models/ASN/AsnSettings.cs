using Common.Services.Settings;

namespace XinJuLi.Models.ASN;

/// <summary>
///     ASN服务配置
/// </summary>
[Configuration("AsnSettings")]
public class AsnSettings : BindableBase
{
    private string _applicationName = "api";

    private bool _continueOnForwardFailure = true;

    private bool _enableForwarding;

    private string _forwardApplicationName = "api";

    private string _forwardServerUrl = "";

    private int _forwardTimeoutSeconds = 30;

    private string _houseCode = "SH_FX";

    private string _httpServerUrl = "http://127.0.0.1:8080";

    private bool _isEnabled = true;

    private string _reviewExitArea = "";

    private string _reviewServerUrl = "";
    private string _systemCode = "SH_FX";
    /// <summary>
    ///     系统编码
    /// </summary>
    public string SystemCode
    {
        get => _systemCode;
        set => SetProperty(ref _systemCode, value);
    }
    /// <summary>
    ///     仓库编码
    /// </summary>
    public string HouseCode
    {
        get => _houseCode;
        set => SetProperty(ref _houseCode, value);
    }
    /// <summary>
    ///     HTTP服务监听地址
    /// </summary>
    public string HttpServerUrl
    {
        get => _httpServerUrl;
        set => SetProperty(ref _httpServerUrl, value);
    }
    /// <summary>
    ///     应用名称
    /// </summary>
    public string ApplicationName
    {
        get => _applicationName;
        set => SetProperty(ref _applicationName, value);
    }
    /// <summary>
    ///     是否启用服务
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }
    /// <summary>
    ///     扫码复核服务器地址
    /// </summary>
    public string ReviewServerUrl
    {
        get => _reviewServerUrl;
        set => SetProperty(ref _reviewServerUrl, value);
    }
    /// <summary>
    ///     扫码复核月台
    /// </summary>
    public string ReviewExitArea
    {
        get => _reviewExitArea;
        set => SetProperty(ref _reviewExitArea, value);
    }
    /// <summary>
    ///     是否启用请求转发
    /// </summary>
    public bool EnableForwarding
    {
        get => _enableForwarding;
        set => SetProperty(ref _enableForwarding, value);
    }
    /// <summary>
    ///     转发目标服务器地址（包含协议和端口，如：http://192.168.1.100:8080）
    /// </summary>
    public string ForwardServerUrl
    {
        get => _forwardServerUrl;
        set => SetProperty(ref _forwardServerUrl, value);
    }
    /// <summary>
    ///     转发目标应用名称
    /// </summary>
    public string ForwardApplicationName
    {
        get => _forwardApplicationName;
        set => SetProperty(ref _forwardApplicationName, value);
    }
    /// <summary>
    ///     转发请求超时时间（秒）
    /// </summary>
    public int ForwardTimeoutSeconds
    {
        get => _forwardTimeoutSeconds;
        set => SetProperty(ref _forwardTimeoutSeconds, value);
    }
    /// <summary>
    ///     转发失败时是否继续本地处理
    /// </summary>
    public bool ContinueOnForwardFailure
    {
        get => _continueOnForwardFailure;
        set => SetProperty(ref _continueOnForwardFailure, value);
    }
}