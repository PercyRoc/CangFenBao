using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace CommonLibrary.Models;

/// <summary>
/// 包裹信息
/// </summary>
public class PackageInfo : IDisposable
{
    private bool _disposed;
    
    /// <summary>
    /// 序号
    /// </summary>
    public int Index { get; set; }
    
    /// <summary>
    /// 条码
    /// </summary>
    public string Barcode { get; set; } = string.Empty;
    
    /// <summary>
    /// 分段码
    /// </summary>
    public string SegmentCode { get; set; } = string.Empty;
    
    /// <summary>
    /// 重量（克）
    /// </summary>
    public float Weight { get; set; }
    
    /// <summary>
    /// 重量显示
    /// </summary>
    public string WeightDisplay => $"{Weight:F2}kg";
    
    /// <summary>
    /// 体积显示
    /// </summary>
    public string VolumeDisplay { get; set; } = string.Empty;
    
    /// <summary>
    /// 格口名称
    /// </summary>
    public int ChuteName { get; set; }
    
    /// <summary>
    /// 状态显示
    /// </summary>
    public string StatusDisplay { get; set; } = string.Empty;
    
    /// <summary>
    /// 处理时间（毫秒）
    /// </summary>
    public double ProcessingTime { get; set; }
    
    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreateTime { get; set; }
    
    /// <summary>
    /// 附加信息
    /// </summary>
    public string Information { get; set; } = string.Empty;
    
    /// <summary>
    /// 错误信息
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 触发时间戳
    /// </summary>
    public DateTime TriggerTimestamp { get; set; }

    /// <summary>
    /// 长度（毫米）
    /// </summary>
    public double? Length { get; set; }

    /// <summary>
    /// 宽度（毫米）
    /// </summary>
    public double? Width { get; set; }

    /// <summary>
    /// 高度（毫米）
    /// </summary>
    public double? Height { get; set; }

    /// <summary>
    /// 体积（立方毫米）
    /// </summary>
    public double? Volume { get; set; }

    /// <summary>
    /// 图像
    /// </summary>
    public Image<Rgba32>? Image { get; set; }
    
    /// <summary>
    /// 图像数据
    /// </summary>
    public byte[]? ImageData { get; set; }
    
    /// <summary>
    /// 图片路径
    /// </summary>
    public string? ImagePath { get; set; }
    
    /// <summary>
    /// 处理状态
    /// </summary>
    public PackageStatus Status { get; set; }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        Image?.Dispose();
        _disposed = true;

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 设置错误信息
    /// </summary>
    public void SetError(string error)
    {
        ErrorMessage = error;
        StatusDisplay = "异常";
    }

    /// <summary>
    /// 设置触发时间戳
    /// </summary>
    public void SetTriggerTimestamp(DateTime timestamp)
    {
        TriggerTimestamp = timestamp;
    }
}