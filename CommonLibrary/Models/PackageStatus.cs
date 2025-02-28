namespace CommonLibrary.Models;

/// <summary>
///     包裹处理状态
/// </summary>
public enum PackageStatus
{
    /// <summary>
    ///     已创建
    /// </summary>
    Created,

    /// <summary>
    ///     测量中
    /// </summary>
    Measuring,

    /// <summary>
    ///     测量成功
    /// </summary>
    MeasureSuccess,

    /// <summary>
    ///     测量失败
    /// </summary>
    MeasureFailed,

    /// <summary>
    ///     称重中
    /// </summary>
    Weighing,

    /// <summary>
    ///     称重成功
    /// </summary>
    WeighSuccess,

    /// <summary>
    ///     称重失败
    /// </summary>
    WeighFailed,

    /// <summary>
    ///     分拣中
    /// </summary>
    Sorting,

    /// <summary>
    ///     分拣完成
    /// </summary>
    SortSuccess,

    /// <summary>
    ///     分拣失败
    /// </summary>
    SortFailed
}