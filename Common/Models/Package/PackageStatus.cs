namespace Common.Models.Package;

/// <summary>
///     包裹处理状态
/// </summary>
public enum PackageStatus
{
    /// <summary>
    ///     包裹创建
    /// </summary>
    Created,

    /// <summary>
    ///     成功
    /// </summary>
    Success,

    /// <summary>
    ///     失败
    /// </summary>
    Failed,

    /// <summary>
    ///     异常
    /// </summary>
    Error,

    /// <summary>
    ///     超时
    /// </summary>
    Timeout,

    /// <summary>
    ///     离线状态
    /// </summary>
    Offline,

    /// <summary>
    ///     等待上包
    /// </summary>
    WaitingForLoading,

    /// <summary>
    ///     拒绝上包
    /// </summary>
    LoadingRejected,

    /// <summary>
    ///     上包已接受，等待最终结果
    /// </summary>
    LoadingAccepted,

    /// <summary>
    ///     上包成功
    /// </summary>
    LoadingSuccess,

    /// <summary>
    ///     上包超时
    /// </summary>
    LoadingTimeout,

    /// <summary>
    ///     上传成功
    /// </summary>
    UploadSuccess,

    /// <summary>
    ///     上传失败
    /// </summary>
    UploadFailed,

    /// <summary>
    ///     无法识别的条码（已处理或上传）
    /// </summary>
    NoRead,

    /// <summary>
    ///     重试已完成（无论成功还是失败，都不再重试）
    /// </summary>
    RetryCompleted,

    /// <summary>
    ///     处理中
    /// </summary>
    Processing
}