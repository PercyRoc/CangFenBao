using Common.Services.Settings;

namespace LosAngelesExpress.Models.Settings;

/// <summary>
/// 洛杉矶菜鸟API配置设置
/// </summary>
[Configuration("CainiaoApiSettings")]
public class CainiaoApiSettings : BindableBase
{
    private bool _usePreEnvironment = true;
    private string _preApiUrl = "https://pre-gxms-us.cainiao.com/api/operate/automation";
    private string _productionApiUrl = "https://gxms-us.cainiao.com/api/operate/automation";
    private string _appSecret = "YGJ7kQcO";
    private string? _workbench = "iVMS_AJ_01";
    private int _timeoutSeconds = 30;
    private int _retryCount = 3;
    private int _retryDelayMilliseconds = 1000;
    private string _bizCode = "789999991";

    /// <summary>
    /// 是否使用预发环境（false为线上环境）
    /// </summary>
    public bool UsePreEnvironment
    {
        get => _usePreEnvironment;
        set => SetProperty(ref _usePreEnvironment, value);
    }

    /// <summary>
    /// 预发环境API地址
    /// </summary>
    public string PreApiUrl
    {
        get => _preApiUrl;
        set => SetProperty(ref _preApiUrl, value);
    }

    /// <summary>
    /// 线上环境API地址
    /// </summary>
    public string ProductionApiUrl
    {
        get => _productionApiUrl;
        set => SetProperty(ref _productionApiUrl, value);
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
    /// 设备编码
    /// </summary>
    public string? Workbench
    {
        get => _workbench;
        set => SetProperty(ref _workbench, value);
    }

    /// <summary>
    /// 请求超时时间（秒）
    /// </summary>
    public int TimeoutSeconds
    {
        get => _timeoutSeconds;
        set => SetProperty(ref _timeoutSeconds, value);
    }

    /// <summary>
    /// 失败重试次数
    /// </summary>
    public int RetryCount
    {
        get => _retryCount;
        set => SetProperty(ref _retryCount, value);
    }

    /// <summary>
    /// 重试间隔（毫秒）
    /// </summary>
    public int RetryDelayMilliseconds
    {
        get => _retryDelayMilliseconds;
        set => SetProperty(ref _retryDelayMilliseconds, value);
    }

    /// <summary>
    /// 业务编码
    /// </summary>
    public string BizCode
    {
        get => _bizCode;
        set => SetProperty(ref _bizCode, value);
    }

    /// <summary>
    /// 获取当前环境的API地址
    /// </summary>
    public string CurrentApiUrl => UsePreEnvironment ? PreApiUrl : ProductionApiUrl;
} 