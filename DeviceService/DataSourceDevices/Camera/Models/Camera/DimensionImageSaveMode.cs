namespace DeviceService.DataSourceDevices.Camera.Models.Camera;

/// <summary>
/// 定义尺寸刻度图的保存模式
/// </summary>
public enum DimensionImageSaveMode
{
    /// <summary>
    /// 不保存任何图像
    /// </summary>
    None,
    /// <summary>
    /// 只保存俯视图
    /// </summary>
    Vertical,
    /// <summary>
    /// 只保存侧视图
    /// </summary>
    Side,
    /// <summary>
    /// 保存俯视图和侧视图
    /// </summary>
    Both
} 