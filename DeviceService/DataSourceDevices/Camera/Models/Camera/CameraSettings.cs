using System.Text.Json.Serialization;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.Camera.Models.Camera.Enums;

namespace DeviceService.DataSourceDevices.Camera.Models.Camera;

/// <summary>
///     相机设置
/// </summary>
[Configuration("CameraSettings")]
public class CameraSettings
{
    /// <summary>
    ///     相机厂商
    /// </summary>
    [JsonPropertyName("Manufacturer")]
    public CameraManufacturer Manufacturer { get; set; }

    /// <summary>
    ///     相机类型
    /// </summary>
    [JsonPropertyName("CameraType")]
    public CameraType CameraType { get; set; }

    /// <summary>
    ///     是否启用条码重复过滤
    /// </summary>
    [JsonPropertyName("BarcodeRepeatFilterEnabled")]
    public bool BarcodeRepeatFilterEnabled { get; set; }

    /// <summary>
    ///     条码重复次数阈值
    /// </summary>
    [JsonPropertyName("RepeatCount")]
    public int RepeatCount { get; set; } = 3;

    /// <summary>
    ///     条码重复时间窗口（毫秒）
    /// </summary>
    [JsonPropertyName("RepeatTimeMs")]
    public int RepeatTimeMs { get; set; } = 1000;

    /// <summary>
    ///     是否启用图像保存
    /// </summary>
    [JsonPropertyName("EnableImageSaving")]
    public bool EnableImageSaving { get; set; }

    /// <summary>
    ///     图像保存路径
    /// </summary>
    [JsonPropertyName("ImageSavePath")]
    public string ImageSavePath { get; set; } = "Images";

    /// <summary>
    ///     图像保存格式
    /// </summary>
    [JsonPropertyName("ImageFormat")]
    public ImageFormat ImageFormat { get; set; } = ImageFormat.Jpeg;
}