namespace Sorting_Car.Models;

/// <summary>
/// 小车配置模型
/// </summary>
public class CarConfig : BindableBase
{
    private double _acceleration = 6;
    private byte _address;
    private int _delay = 350;
    private string _name = string.Empty;
    private int _speed = 500;
    private int _time = 500;

    /// <summary>
    ///     加速度
    /// </summary>
    public double Acceleration
    {
        get => _acceleration;
        set => SetProperty(ref _acceleration, value);
    }

    /// <summary>
    ///     小车地址
    /// </summary>
    public byte Address
    {
        get => _address;
        set => SetProperty(ref _address, value);
    }

    /// <summary>
    ///     延迟运行时间(ms)
    /// </summary>
    public int Delay
    {
        get => _delay;
        set => SetProperty(ref _delay, value);
    }

    /// <summary>
    ///     小车名称
    /// </summary>
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    /// <summary>
    ///     运行速度
    /// </summary>
    public int Speed
    {
        get => _speed;
        set => SetProperty(ref _speed, value);
    }

    /// <summary>
    ///     运行时间(ms)
    /// </summary>
    public int Time
    {
        get => _time;
        set => SetProperty(ref _time, value);
    }
} 