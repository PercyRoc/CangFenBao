using DeviceService.DataSourceDevices.Weight;

namespace CangFenBao.SDK;

/// <summary>
/// 重量服务的配置参数。
/// </summary>
public class WeightServiceSettings
{
    /// <summary>
    /// 是否启用重量服务。
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 串口参数。
    /// </summary>
    public SerialPortParams SerialPortParams { get; set; } = new();

    /// <summary>
    /// 最小有效重量阈值（单位：克）。低于此值的重量将被视为无效。
    /// </summary>
    public double MinimumWeightGrams { get; set; } = 20.0;

    /// <summary>
    /// 重量更新事件的最小报告间隔（单位：毫秒）。
    /// 用于控制事件频率，防止事件风暴。
    /// </summary>
    public int ReportIntervalMilliseconds { get; set; } = 100;

    /// <summary>
    /// 用于判断重量稳定性的滑动窗口样本数。
    /// </summary>
    public int StableSampleCount { get; set; } = 5;

    /// <summary>
    /// 判断重量稳定的阈值（单位：克）。
    /// 窗口中最后一个值与所有先前值的差的绝对值都必须小于此阈值。
    /// </summary>
    public double StableThresholdGrams { get; set; } = 10.0;

    /// <summary>
    /// 融合重量时，相对于相机数据时间戳的查找范围下限（单位：毫秒）。
    /// 通常是一个负值，表示在相机数据到达之前的时间。
    /// </summary>
    public int FusionTimeRangeLowerMs { get; set; } = -500;

    /// <summary>
    /// 融合重量时，相对于相机数据时间戳的查找范围上限（单位：毫秒）。
    /// 通常是一个正值，表示在相机数据到达之后的时间。
    /// </summary>
    public int FusionTimeRangeUpperMs { get; set; } = 500;
} 