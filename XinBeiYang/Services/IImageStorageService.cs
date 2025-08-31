using System.Windows.Media.Imaging;

namespace XinBeiYang.Services;

/// <summary>
///     定义图像存储服务的接口契约。
/// </summary>
public interface IImageStorageService
{
    /// <summary>
    ///     异步保存带水印的图像（水印位于左上角，绿色字体，包含条码/重量/体积/时间）。
    /// </summary>
    /// <param name="image">要保存的 BitmapSource 图像。</param>
    /// <param name="barcode">条码。</param>
    /// <param name="weightKg">重量（kg）。</param>
    /// <param name="lengthCm">长度（cm）。</param>
    /// <param name="widthCm">宽度（cm）。</param>
    /// <param name="heightCm">高度（cm）。</param>
    /// <param name="createTime">创建时间。</param>
    /// <returns>保存路径，失败返回 null。</returns>
    Task<string?> SaveImageWithWatermarkAsync(
        BitmapSource? image,
        string barcode,
        double weightKg,
        double? lengthCm,
        double? widthCm,
        double? heightCm,
        DateTime createTime);

    /// <summary>
    ///     异步保存原始图像（不加水印）。
    /// </summary>
    /// <param name="image">要保存的 BitmapSource 图像。</param>
    /// <param name="barcode">条码。</param>
    /// <param name="createTime">创建时间。</param>
    /// <returns>保存路径，失败返回 null。</returns>
    Task<string?> SaveOriginalAsync(BitmapSource? image, string barcode, DateTime createTime);
}