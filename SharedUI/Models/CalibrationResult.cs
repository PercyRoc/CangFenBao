using JetBrains.Annotations;

namespace SharedUI.Models;

public class CalibrationResult
{
    [UsedImplicitly] public DateTime Timestamp { get; set; }

    [UsedImplicitly] public string PhotoelectricName { get; set; } = string.Empty;

    [UsedImplicitly] public DateTime TriggerTime { get; set; }

    [UsedImplicitly] public DateTime SortingTime { get; set; }

    [UsedImplicitly] public double MeasuredDelay { get; set; }

    /// <summary>
    ///     触发时间标定结果（触发信号 -> 包裹处理时间）
    /// </summary>
    [UsedImplicitly]
    public double TriggerTimeDelay { get; set; }

    /// <summary>
    ///     分拣时间标定结果（触发信号 -> 分拣信号时间）
    /// </summary>
    [UsedImplicitly]
    public double SortingTimeDelay { get; set; }

    /// <summary>
    ///     标定模式
    /// </summary>
    [UsedImplicitly]
    public CalibrationMode Mode { get; set; }
}