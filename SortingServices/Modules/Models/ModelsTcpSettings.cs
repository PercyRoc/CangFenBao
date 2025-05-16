using Common.Services.Settings;

namespace SortingServices.Modules.Models;

[Configuration("ModelsTcpSettings")]
public class ModelsTcpSettings : BindableBase
{
    private string _address = "127.0.0.1";
    private int _port = 8080;
    private int _minTime = 100; // 默认值示例，您可以按需修改
    private int _maxTime = 1000; // 默认值示例，您可以按需修改
    private int _exceptionChute = 99; // 默认值示例，您可以按需修改

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

    /// <summary>
    ///     最小时间 (例如：毫秒)
    /// </summary>
    public int MinTime
    {
        get => _minTime;
        set => SetProperty(ref _minTime, value);
    }

    /// <summary>
    ///     最大时间 (例如：毫秒)
    /// </summary>
    public int MaxTime
    {
        get => _maxTime;
        set => SetProperty(ref _maxTime, value);
    }

    /// <summary>
    ///     异常格口号
    /// </summary>
    public int ExceptionChute
    {
        get => _exceptionChute;
        set => SetProperty(ref _exceptionChute, value);
    }
}