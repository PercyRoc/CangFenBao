using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace CommonLibrary.Models;

public class PackageInfo : IDisposable
{
    private bool _disposed;
    public int Index { get; set; }
    public string Barcode { get; set; } = string.Empty;
    public string SegmentCode { get; set; } = string.Empty;
    public double Weight { get; set; }
    public string WeightDisplay => $"{Weight:F2}kg";
    public string VolumeDisplay { get; set; } = string.Empty;
    public string ChuteName { get; set; } = string.Empty;
    public string StatusDisplay { get; set; } = string.Empty;
    public double ProcessingTime { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Information { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;

    /// <summary>
    ///     触发时间戳
    /// </summary>
    public DateTime TriggerTimestamp { get; set; }

    /// <summary>
    ///     长度（毫米）
    /// </summary>
    public double? Length { get; set; }

    /// <summary>
    ///     宽度（毫米）
    /// </summary>
    public double? Width { get; set; }

    /// <summary>
    ///     高度（毫米）
    /// </summary>
    public double? Height { get; set; }

    /// <summary>
    ///     图像
    /// </summary>
    public Image<Rgba32>? Image { get; set; }

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
        Error = error;
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