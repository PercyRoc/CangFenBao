using System.Windows.Media.Imaging;

namespace XinBeiYang.Services;

/// <summary>
///     定义图像存储服务的接口契约。
/// </summary>
public interface IImageStorageService
{
    /// <summary>
    ///     异步保存提供的图像。
    /// </summary>
    /// <param name="image">要保存的 BitmapSource 图像。</param>
    /// <param name="barcode">与图像关联的条码，用于命名。</param>
    /// <param name="createTime">包裹的创建时间，用于命名和组织。</param>
    /// <returns>保存的图像文件的完整路径，如果保存失败则返回 null。</returns>
    Task<string?> SaveImageAsync(BitmapSource? image, string barcode, DateTime createTime);
}