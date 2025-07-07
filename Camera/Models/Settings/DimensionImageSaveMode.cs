namespace Camera.Models.Settings;

/// <summary>
/// 定义尺寸刻度图的保存模式 (本地定义, 支持多选)
/// </summary>
[Flags]
public enum DimensionImageSaveMode
{
    None = 0,
    Vertical = 1,     // 001
    Side = 2,         // 010
    Original = 4,     // 100
}