namespace Common.Models.Package;

/// <summary>
///     包裹处理状态
/// </summary>
public enum PackageStatus
{
    /// <summary>
    ///     初始状态
    /// </summary>
    Initial = 0,

    /// <summary>
    ///     包裹创建
    /// </summary>
    Created = 1,

    /// <summary>
    ///     正在测量
    /// </summary>
    Measuring = 2,

    /// <summary>
    ///     测量成功
    /// </summary>
    MeasureSuccess = 3,

    /// <summary>
    ///     测量失败
    /// </summary>
    MeasureFailed = 4,

    /// <summary>
    ///     正在称重
    /// </summary>
    Weighing = 5,

    /// <summary>
    ///     称重成功
    /// </summary>
    WeighSuccess = 6,

    /// <summary>
    ///     称重失败
    /// </summary>
    WeighFailed = 7,

    /// <summary>
    ///     等待分配格口
    /// </summary>
    WaitingForChute = 8,

    /// <summary>
    ///     等待模组带处理
    /// </summary>
    WaitingForModule = 9,

    /// <summary>
    ///     正在分拣
    /// </summary>
    Sorting = 10,

    /// <summary>
    ///     分拣成功
    /// </summary>
    SortSuccess = 11,

    /// <summary>
    ///     分拣失败
    /// </summary>
    SortFailed = 12,

    /// <summary>
    ///     分拣完成
    /// </summary>
    Completed = 13,

    /// <summary>
    ///     处理异常
    /// </summary>
    Error = 14,

    /// <summary>
    ///     超时
    /// </summary>
    Timeout = 15,

    /// <summary>
    ///     离线状态
    /// </summary>
    Offline = 16,

    /// <summary>
    ///     已处理
    /// </summary>
    Processed = 17
}