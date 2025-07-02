namespace SortingServices.Car;

/// <summary>
///     小车分拣序列
/// </summary>
public class SortingSequence
{
    /// <summary>
    ///     小车地址（作为唯一标识）
    /// </summary>
    public required byte CarAddress { get; set; }

    /// <summary>
    ///     是否正向
    /// </summary>
    public required bool IsForward { get; set; }

    /// <summary>
    ///     指令延迟时间（毫秒）
    /// </summary>
    public int CommandDelay { get; set; }
}