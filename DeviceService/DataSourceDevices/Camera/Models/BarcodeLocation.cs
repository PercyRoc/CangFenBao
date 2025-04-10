namespace DeviceService.DataSourceDevices.Camera.Models;

/// <summary>
///     条码位置信息
/// </summary>
public class BarcodeLocation
{
    /// <summary>
    ///     条码内容
    /// </summary>
    internal string Code { get; set; } = string.Empty;

    /// <summary>
    ///     条码类型
    /// </summary>
    public string Type { get; set; } = string.Empty;
    
    /// <summary>
    /// 标准化左边界坐标（0-1）
    /// </summary>
    public float NormalizedLeft { get; set; }
    
    /// <summary>
    /// 标准化上边界坐标（0-1）
    /// </summary>
    public float NormalizedTop { get; set; }
    
    /// <summary>
    /// 标准化宽度（0-1）
    /// </summary>
    public float NormalizedWidth { get; set; }
    
    /// <summary>
    /// 标准化高度（0-1）
    /// </summary>
    public float NormalizedHeight { get; set; }
    
    /// <summary>
    /// 创建标准化坐标的BarcodeLocation实例
    /// </summary>
    public static BarcodeLocation FromNormalizedValues(float left, float top, float width, float height)
    {
        return new BarcodeLocation
        {
            NormalizedLeft = left,
            NormalizedTop = top,
            NormalizedWidth = width,
            NormalizedHeight = height
        };
    }
}