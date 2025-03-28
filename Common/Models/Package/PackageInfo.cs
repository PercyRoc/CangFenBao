using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Common.Models.Package;

/// <summary>
///     包裹信息
/// </summary>
public class PackageInfo : IDisposable
{
    private bool _disposed;

    /// <summary>
    ///     构造函数
    /// </summary>
    public PackageInfo()
    {
        // 设置创建时间为当前时间
        CreateTime = DateTime.Now;
    }

    /// <summary>
    ///     序号
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    ///     条码
    /// </summary>
    public string Barcode { get; set; } = string.Empty;

    /// <summary>
    ///     分段码
    /// </summary>
    public string SegmentCode { get; set; } = string.Empty;

    /// <summary>
    ///     重量（千克）
    /// </summary>
    public float Weight { get; set; }

    /// <summary>
    ///     重量显示
    /// </summary>
    public string WeightDisplay => $"{Weight:F2}kg";

    /// <summary>
    ///     体积显示
    /// </summary>
    public string VolumeDisplay => Length.HasValue && Width.HasValue && Height.HasValue 
        ? $"{Length:F1}*{Width:F1}*{Height:F1}"
        : string.Empty;

    /// <summary>
    ///     格口名称
    /// </summary>
    public int ChuteName { get; set; }

    /// <summary>
    ///     原始格口名称（当格口被锁定时，记录原始分配的格口）
    /// </summary>
    public int OriginalChuteName { get; set; }

    /// <summary>
    ///     状态显示
    /// </summary>
    public string StatusDisplay { get; set; } = string.Empty;

    /// <summary>
    ///     处理时间（毫秒）
    /// </summary>
    public double ProcessingTime { get; set; }

    /// <summary>
    ///     创建时间
    /// </summary>
    public DateTime CreateTime { get; set; }

    /// <summary>
    ///     附加信息
    /// </summary>
    public string? Information { get; set; } = string.Empty;

    /// <summary>
    ///     错误信息
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    ///     触发时间戳
    /// </summary>
    public DateTime TriggerTimestamp { get; set; }

    /// <summary>
    ///     长度（厘米）
    /// </summary>
    public double? Length { get; set; }

    /// <summary>
    ///     宽度（厘米）
    /// </summary>
    public double? Width { get; set; }

    /// <summary>
    ///     高度（厘米）
    /// </summary>
    public double? Height { get; set; }

    /// <summary>
    ///     体积（立方厘米）
    /// </summary>
    public double? Volume { get; set; }

    /// <summary>
    ///     图像
    /// </summary>
    public Image<Rgba32>? Image { get; set; }

    /// <summary>
    ///     图片路径
    /// </summary>
    public string? ImagePath { get; set; }

    /// <summary>
    ///     处理状态
    /// </summary>
    public PackageStatus Status { get; set; }

    /// <summary>
    ///     包裹计数
    /// </summary>
    public int PackageCount { get; set; }

    /// <summary>
    ///     释放资源
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        Image?.Dispose();
        _disposed = true;

        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     设置错误信息
    /// </summary>
    public void SetError(string error)
    {
        ErrorMessage = error;
        StatusDisplay = "异常";
    }

    /// <summary>
    ///     设置触发时间戳
    /// </summary>
    public void SetTriggerTimestamp(DateTime timestamp)
    {
        TriggerTimestamp = timestamp;
    }
}