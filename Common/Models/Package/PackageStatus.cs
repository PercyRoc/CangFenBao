namespace Common.Models.Package;

/// <summary>
///     包裹处理状态
/// </summary>
public enum PackageStatus
{
    /// <summary>
    ///     包裹创建
    /// </summary>
    Created = 0,

    /// <summary>
    ///     正在测量
    /// </summary>
    Measuring = 1,

    /// <summary>
    ///     测量成功
    /// </summary>
    MeasureSuccess = 2,

    /// <summary>
    ///     测量失败
    /// </summary>
    MeasureFailed = 3,

    /// <summary>
    ///     正在称重
    /// </summary>
    Weighing = 4,

    /// <summary>
    ///     称重成功
    /// </summary>
    WeighSuccess = 5,

    /// <summary>
    ///     称重失败
    /// </summary>
    WeighFailed = 6,

    /// <summary>
    ///     等待分配格口
    /// </summary>
    WaitingForChute = 7,
    /// <summary>
    ///     正在分拣
    /// </summary>
    Sorting = 8,

    /// <summary>
    ///     分拣成功
    /// </summary>
    SortSuccess = 9,

    /// <summary>
    ///     分拣失败
    /// </summary>
    SortFailed = 10,

    /// <summary>
    ///     处理异常
    /// </summary>
    Error = 11,

    /// <summary>
    ///     超时
    /// </summary>
    Timeout = 12,

    /// <summary>
    ///     离线状态
    /// </summary>
    Offline = 13,

    /// <summary>
    ///     等待上包
    /// </summary>
    WaitingForLoading = 14,

    /// <summary>
    ///     拒绝上包
    /// </summary>
    LoadingRejected = 15,

    /// <summary>
    ///     上包成功
    /// </summary>
    LoadingSuccess = 16,

    /// <summary>
    ///     上包超时
    /// </summary>
    LoadingTimeout = 17,
}