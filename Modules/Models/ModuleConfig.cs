using Common.Services.Settings;
using Prism.Mvvm;

namespace ShanghaiModuleBelt.Models;

[Configuration("ModuleConfig")]
public class ModuleConfig : BindableBase
{
    private string _address = "127.0.0.1";
    private int _exceptionChute = 10; // 默认异常格口号
    private int _maxWaitTime = 5000;
    private int _minWaitTime = 1000;
    private int _port = 8080;
    private int _serverTimeout = 3000; // 默认服务器通讯超时时间（毫秒）
    // 站点与 Token 不再在本地配置中管理

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

    // SiteCode 与 Token 已移除
}