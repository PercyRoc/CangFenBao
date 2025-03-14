using Common.Services.Settings;
using Prism.Mvvm;

namespace Modules.Models;

[Configuration("ModuleConfig")]
public class ModuleConfig : BindableBase
{
    private string _address = "127.0.0.1";
    private int _exceptionChute = 10; // 默认异常格口号
    private int _maxWaitTime = 5000;
    private int _minWaitTime = 1000;
    private int _port = 8080;
    private int _serverTimeout = 3000; // 默认服务器通讯超时时间（毫秒）
    private string _siteCode = "1002"; // 默认为深圳站点
    private string _token = "CADD04F33F0944E187EB4EB873EE23CD"; // 默认Token值

    public string Address
    {
        get => _address;
        set => SetProperty(ref _address, value);
    }

    public int Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    public int MinWaitTime
    {
        get => _minWaitTime;
        set => SetProperty(ref _minWaitTime, value);
    }

    public int MaxWaitTime
    {
        get => _maxWaitTime;
        set => SetProperty(ref _maxWaitTime, value);
    }

    public string SiteCode
    {
        get => _siteCode;
        set => SetProperty(ref _siteCode, value);
    }

    public int ServerTimeout
    {
        get => _serverTimeout;
        set => SetProperty(ref _serverTimeout, value);
    }

    public int ExceptionChute
    {
        get => _exceptionChute;
        set => SetProperty(ref _exceptionChute, value);
    }

    public string Token
    {
        get => _token;
        set => SetProperty(ref _token, value);
    }
}