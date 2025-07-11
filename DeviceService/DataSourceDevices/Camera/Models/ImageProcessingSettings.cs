namespace DeviceService.DataSourceDevices.Camera.Models;

/// <summary>
///     图像处理相关设置
/// </summary>
public class ImageProcessingSettings
{
    /// <summary>
    ///     是否保存图片
    /// </summary>
    public bool SaveImages { get; set; }

    /// <summary>
    ///     图片保存路径
    /// </summary>
    public string ImageSavePath { get; set; } = string.Empty;

    /// <summary>
    ///     是否添加水印
    /// </summary>
    public bool AddWatermark { get; set; }

    /// <summary>
    ///     水印格式
    /// </summary>
    public string WatermarkFormat { get; set; } = "SN: {barcode}\r\n{dateTime}";
} 