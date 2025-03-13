using SixLabors.ImageSharp;

namespace DeviceService.DataSourceDevices.Camera.Models;

/// <summary>
///     条码位置信息
/// </summary>
public class BarcodeLocation(List<Point> points)
{
    /// <summary>
    ///     条码内容
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>
    ///     条码类型
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    ///     条码区域的顶点坐标
    /// </summary>
    public List<Point> Points { get; set; } = points;
}