namespace SharedUI.Models;

/// <summary>
///     Represents the type of calibration to be performed.
/// </summary>
public enum CalibrationMode
{
    /// <summary>
    ///     Calibrates the time from the trigger signal to the sorting signal.
    ///     (触发信号 -> 分拣信号)
    /// </summary>
    SortingTime,

    /// <summary>
    ///     Calibrates the time from the trigger signal to the start of package processing.
    ///     (触发信号 -> 包裹处理)
    /// </summary>
    TriggerTime,

    /// <summary>
    ///     Complete calibration flow: trigger time + sorting time in one process.
    ///     (完整标定流程：触发时间 + 分拣时间)
    /// </summary>
    CompleteFlow
}

/// <summary>
/// Represents a generic item that can be calibrated in the calibration dialog.
/// </summary>
public class CalibrationTarget
{
    /// <summary>
    /// The type of calibration this target is for.
    /// </summary>
    public CalibrationMode Mode { get; set; } = CalibrationMode.CompleteFlow;

    /// <summary>
    /// A unique identifier for the target, used to match signals. e.g., "Trigger" or "Photoelectric1".
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// The name to be displayed in the UI dropdown. e.g., "触发光电" or "分拣光电1 (格口 1-2)".
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// The lower bound of the valid time range for sorting, in milliseconds.
    /// </summary>
    public double TimeRangeLower { get; set; }

    /// <summary>
    /// The upper bound of the valid time range for sorting, in milliseconds.
    /// </summary>
    public double TimeRangeUpper { get; set; }

    /// <summary>
    /// The delay before the sorting action is executed, in milliseconds.
    /// </summary>
    public double SortingDelay { get; set; }

    /// <summary>
    /// The delay before the sorting mechanism is reset, in milliseconds.
    /// </summary>
    public double ResetDelay { get; set; }
} 