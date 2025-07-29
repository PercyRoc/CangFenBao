using Common.Services.Settings;

namespace DeviceService.DataSourceDevices.Weight;

/// <summary>
///     重量设置
/// </summary>
[Configuration("WeightSettings")]
public class WeightSettings
{
    /// <summary>
    ///     是否启用重量融合
    /// </summary>
    public bool EnableWeightFusion { get; set; }

    /// <summary>
    ///     称重类型
    /// </summary>
    public WeightType WeightType { get; set; }

    /// <summary>
    ///     稳定性判断所需的样本数量（仅静态称重时有效）
    /// </summary>
    public int StableCheckCount { get; set; } = 5;

    /// <summary>
    ///     串口参数
    /// </summary>
    public SerialPortParams SerialPortParams { get; set; } = new();

    /// <summary>
    ///     时间范围下限（毫秒）
    /// </summary>
    public int TimeRangeLower { get; set; } = -200;

    /// <summary>
    ///     时间范围上限（毫秒）
    /// </summary>
    public int TimeRangeUpper { get; set; } = 500;

    /// <summary>
    ///     最小重量（克）
    /// </summary>
    public double MinimumWeight { get; set; } = 10.0;
}