using Prism.Mvvm;

namespace DongtaiFlippingBoardMachine.Models;

/// <summary>
///     翻板机配置项
/// </summary>
public class PlateTurnoverItem : BindableBase
{
    private string _index = string.Empty;
    private int _mappingChute;
    private string? _tcpAddress;
    private string? _ioPoint;
    private double _distance;
    private double _delayFactor = 0.5;
    private int _magnetTime = 200;

    /// <summary>
    ///     序号
    /// </summary>
    public string Index
    {
        get => _index;
        set => SetProperty(ref _index, value);
    }

    /// <summary>
    ///     映射格口
    /// </summary>
    public int MappingChute
    {
        get => _mappingChute;
        set => SetProperty(ref _mappingChute, value);
    }

    /// <summary>
    ///     TCP地址
    /// </summary>
    public string? TcpAddress
    {
        get => _tcpAddress;
        set => SetProperty(ref _tcpAddress, value);
    }

    /// <summary>
    ///     IO点
    /// </summary>
    public string? IoPoint
    {
        get => _ioPoint;
        set => SetProperty(ref _ioPoint, value);
    }

    /// <summary>
    ///     距离（用于计算光电触发次数）
    /// </summary>
    public double Distance
    {
        get => _distance;
        set => SetProperty(ref _distance, value);
    }

    /// <summary>
    ///     延迟系数（0-1之间）
    /// </summary>
    public double DelayFactor
    {
        get => _delayFactor;
        set => SetProperty(ref _delayFactor, value);
    }

    /// <summary>
    ///     磁铁吸合时间（毫秒）
    /// </summary>
    public int MagnetTime
    {
        get => _magnetTime;
        set => SetProperty(ref _magnetTime, value);
    }
}