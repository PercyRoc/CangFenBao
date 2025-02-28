using System.ComponentModel;

namespace CommonLibrary.Models.Settings.Camera.Enums;

/// <summary>
///     图像保存格式
/// </summary>
public enum ImageFormat
{
    /// <summary>
    ///     JPEG格式
    /// </summary>
    [Description("JPEG")] Jpeg,

    /// <summary>
    ///     PNG格式
    /// </summary>
    [Description("PNG")] Png,

    /// <summary>
    ///     BMP格式
    /// </summary>
    [Description("BMP")] Bmp,

    /// <summary>
    ///     TIFF格式
    /// </summary>
    [Description("TIFF")] Tiff
}