using System.Windows.Media.Imaging;

namespace XinBa.Services;

/// <summary>
///     提供二维码生成功能的服务接口
/// </summary>
public interface IQrCodeService
{
    /// <summary>
    ///     根据给定的文本生成二维码
    /// </summary>
    /// <param name="text">要编码到二维码中的文本</param>
    /// <returns>生成的二维码图像，如果生成失败则返回 null</returns>
    BitmapSource? Generate(string text);
}