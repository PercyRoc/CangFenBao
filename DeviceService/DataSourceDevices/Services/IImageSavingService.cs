using System.Windows.Media.Imaging;

namespace DeviceService.DataSourceDevices.Services;

/// <summary>
///     图像保存服务的接口。
/// </summary>
public interface IImageSavingService
{
    /// <summary>
    ///     异步保存提供的图像到配置的位置。
    /// </summary>
    /// <param name="image">要保存的图像。</param>
    /// <param name="barcode">与图像关联的条码（可以为null或"NOREAD"）。</param>
    /// <param name="timestamp">图像的时间戳。</param>
    /// <returns>图像保存的完整路径，如果保存失败或被禁用则返回null。</returns>
    Task<string?> SaveImageAsync(BitmapSource? image, string? barcode, DateTime timestamp);

    /// <summary>
    ///     根据配置、条码和时间戳生成保存图像的潜在完整路径，
    ///     而不实际保存文件或创建目录。
    /// </summary>
    /// <param name="barcode">与图像关联的条码（可以为null或"NOREAD"）。</param>
    /// <param name="timestamp">图像的时间戳。</param>
    /// <returns>图像将要保存的潜在完整路径，如果保存被禁用或无法确定路径则返回null。</returns>
    string? GenerateImagePath(string? barcode, DateTime timestamp);

    /// <summary>
    ///     手动触发清理超过保留期限的旧图像文件。
    /// </summary>
    /// <param name="retentionDays">保留图像的天数。如果为null，则使用默认保留期限（7天）。</param>
    /// <returns>表示异步清理操作的任务。</returns>
    Task CleanupOldImagesAsync(int? retentionDays = null);
}