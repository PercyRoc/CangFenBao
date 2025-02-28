using System.ComponentModel;
using CommonLibrary.Models.Settings;
using Prism.Mvvm;

namespace Presentation_BenFly.Models.Upload;

public enum BenNiaoEnvironment
{
    [Description("测试环境")] Test,

    [Description("生产环境")] Production
}

[Configuration("UploadSettings")]
public class UploadConfiguration : BindableBase
{
    private string _benNiaoAppId = string.Empty;

    private string _benNiaoAppSecret = string.Empty;

    private string _benNiaoDistributionCenterName = string.Empty;
    private BenNiaoEnvironment _benNiaoEnvironment;

    private string _benNiaoFtpHost = string.Empty;
    private string _benNiaoFtpPassword = string.Empty;
    private int _benNiaoFtpPort = 22;

    private string _benNiaoFtpUsername = string.Empty;

    private string _deviceId = string.Empty;

    private int _preReportUpdateIntervalSeconds = 60;

    public BenNiaoEnvironment BenNiaoEnvironment
    {
        get => _benNiaoEnvironment;
        set => SetProperty(ref _benNiaoEnvironment, value);
    }

    public string BenNiaoAppId
    {
        get => _benNiaoAppId;
        set => SetProperty(ref _benNiaoAppId, value);
    }

    public string BenNiaoAppSecret
    {
        get => _benNiaoAppSecret;
        set => SetProperty(ref _benNiaoAppSecret, value);
    }

    public string BenNiaoDistributionCenterName
    {
        get => _benNiaoDistributionCenterName;
        set => SetProperty(ref _benNiaoDistributionCenterName, value);
    }

    public string BenNiaoFtpHost
    {
        get => _benNiaoFtpHost;
        set => SetProperty(ref _benNiaoFtpHost, value);
    }

    public int BenNiaoFtpPort
    {
        get => _benNiaoFtpPort;
        set => SetProperty(ref _benNiaoFtpPort, value);
    }

    public string BenNiaoFtpUsername
    {
        get => _benNiaoFtpUsername;
        set => SetProperty(ref _benNiaoFtpUsername, value);
    }

    public string BenNiaoFtpPassword
    {
        get => _benNiaoFtpPassword;
        set => SetProperty(ref _benNiaoFtpPassword, value);
    }

    public int PreReportUpdateIntervalSeconds
    {
        get => _preReportUpdateIntervalSeconds;
        set => SetProperty(ref _preReportUpdateIntervalSeconds, value);
    }

    public string DeviceId
    {
        get => _deviceId;
        set => SetProperty(ref _deviceId, value);
    }
}