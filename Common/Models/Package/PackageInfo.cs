using System.Windows.Media.Imaging;
using System.Threading;
using System.Collections.Generic;

namespace Common.Models.Package;

/// <summary>
///     包裹信息
/// </summary>
public class PackageInfo : IDisposable
{
    private static int _currentIndex;
    private bool _disposed;

    private static readonly Dictionary<PackageStatus, string> DefaultStatusDisplays = new()
    {
        { PackageStatus.Created, "已创建" },
        { PackageStatus.Measuring, "正在测量" },
        { PackageStatus.MeasureSuccess, "测量成功" },
        { PackageStatus.MeasureFailed, "测量失败" },
        { PackageStatus.Weighing, "正在称重" },
        { PackageStatus.WeighSuccess, "称重成功" },
        { PackageStatus.WeighFailed, "称重失败" },
        { PackageStatus.WaitingForChute, "等待分配" },
        { PackageStatus.Sorting, "正在分拣" },
        { PackageStatus.SortSuccess, "分拣成功" },
        { PackageStatus.SortFailed, "分拣失败" },
        { PackageStatus.Timeout, "处理超时" },
        { PackageStatus.Offline, "设备离线" },
        { PackageStatus.Error, "异常" },
        { PackageStatus.WaitingForLoading, "等待上包" },
        { PackageStatus.LoadingRejected, "拒绝上包" },
        { PackageStatus.LoadingSuccess, "上包成功" },
        { PackageStatus.LoadingTimeout, "上包超时" }
    };

    /// <summary>
    ///     构造函数
    /// </summary>
    private PackageInfo()
    {
        // 设置创建时间为当前时间
        CreateTime = DateTime.Now;
        // Initial status is now set in the Create() factory method
    }

    /// <summary>
    ///     创建 PackageInfo 的新实例并自动递增序号。
    /// </summary>
    /// <returns>新的 PackageInfo 实例。</returns>
    public static PackageInfo Create()
    {
        var newIndex = Interlocked.Increment(ref _currentIndex); // 使用 Interlocked 保证线程安全
        var package = new PackageInfo
        {
            Index = newIndex
        };
        package.SetStatus(PackageStatus.Created); // Set initial status after creation
        return package;
    }

    /// <summary>
    ///     序号
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    ///     条码
    /// </summary>
    public string Barcode { get; private set; } = string.Empty;

    /// <summary>
    ///     段码
    /// </summary>
    public string SegmentCode { get; private set; } = string.Empty;

    /// <summary>
    ///     重量（千克）
    /// </summary>
    public double Weight { get; private set; }

    /// <summary>
    ///     重量显示
    /// </summary>
    public string WeightDisplay => $"{Weight:F2}kg";

    /// <summary>
    ///     体积显示
    /// </summary>
    public string VolumeDisplay => Length.HasValue && Width.HasValue && Height.HasValue 
        ? $"{Length:F1}cm*{Width:F1}cm*{Height:F1}cm"
        : string.Empty;

    /// <summary>
    ///     格口号
    /// </summary>
    public int ChuteNumber { get; private set; }

    /// <summary>
    ///     原始格口号（当格口被锁定时，记录原始分配的格口）
    /// </summary>
    public int OriginalChuteNumber { get; private set; }

    /// <summary>
    ///     状态显示
    /// </summary>
    public string StatusDisplay { get; private set; } = string.Empty;

    /// <summary>
    ///     处理时间（毫秒）
    /// </summary>
    public double ProcessingTime { get; set; }

    /// <summary>
    ///     创建时间
    /// </summary>
    public DateTime CreateTime { get; set; }

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
    public double? Length { get; private set; }

    /// <summary>
    ///     宽度（厘米）
    /// </summary>
    public double? Width { get; private set; }

    /// <summary>
    ///     高度（厘米）
    /// </summary>
    public double? Height { get; private set; }

    /// <summary>
    ///     体积（立方厘米）
    /// </summary>
    public double? Volume { get; private set; }

    /// <summary>
    ///     图像 (WPF BitmapSource format)
    /// </summary>
    public BitmapSource? Image { get; set; }

    /// <summary>
    ///     图片路径
    /// </summary>
    public string? ImagePath { get; set; }

    /// <summary>
    ///     处理状态
    /// </summary>
    public PackageStatus Status { get; private set; }

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

        _disposed = true;

        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     释放图像资源
    /// </summary>
    /// <remarks>
    ///     BitmapSource 本身不是 IDisposable。将其设置为 null 有助于垃圾回收器更快地回收内存。
    ///     如果 BitmapSource 是从可释放资源（例如 Stream 或 Bitmap）创建的，
    ///     则可能需要在调用此方法之前显式释放该资源，或者根据创建上下文在此处添加逻辑。
    /// </remarks>
    public void ReleaseImage()
    {
        Image = null;
    }

    /// <summary>
    ///     设置条码。
    /// </summary>
    /// <param name="barcode">条码</param>
    public void SetBarcode(string? barcode)
    {
        Barcode = barcode ?? string.Empty;
    }

    /// <summary>
    ///     设置段码。
    /// </summary>
    /// <param name="segmentCode">段码</param>
    public void SetSegmentCode(string? segmentCode)
    {
        SegmentCode = segmentCode ?? string.Empty;
    }

    /// <summary>
    ///     设置重量。
    /// </summary>
    /// <param name="weight">重量（千克）</param>
    public void SetWeight(double weight)
    {
        Weight = weight;
    }

    /// <summary>
    ///     设置尺寸。
    /// </summary>
    /// <param name="length">长度（厘米）</param>
    /// <param name="width">宽度（厘米）</param>
    /// <param name="height">高度（厘米）</param>
    public void SetDimensions(double length, double width, double height)
    {
        Length = length;
        Width = width;
        Height = height;
    }

    /// <summary>
    ///     设置格口号。
    /// </summary>
    /// <param name="chuteNumber">格口号</param>
    /// <param name="originalChuteNumber">原始格口号 (可选)</param>
    public void SetChute(int chuteNumber, int? originalChuteNumber = null)
    {
        ChuteNumber = chuteNumber;
        OriginalChuteNumber = originalChuteNumber ?? chuteNumber;
    }

    /// <summary>
    ///     设置处理状态。如果未提供 statusDisplay，则使用默认值。
    /// </summary>
    /// <param name="status">新的状态</param>
    /// <param name="statusDisplay">状态的显示文本 (可选)</param>
    public void SetStatus(PackageStatus status, string? statusDisplay = null)
    {
        Status = status;
        StatusDisplay = !string.IsNullOrEmpty(statusDisplay)
                        ? statusDisplay
                        : DefaultStatusDisplays.GetValueOrDefault(status, status.ToString()); // Use default or enum name as fallback
        if (status == PackageStatus.Error && string.IsNullOrEmpty(ErrorMessage))
        {
            ErrorMessage = StatusDisplay;
        }
    }

    /// <summary>
    ///     设置图像信息。
    /// </summary>
    /// <param name="image">图像的 BitmapSource</param>
    /// <param name="imagePath">图像文件路径</param>
    public void SetImage(BitmapSource? image, string? imagePath)
    {
        Image = image;
        ImagePath = imagePath;
    }

    /// <summary>
    ///     设置触发时间戳
    /// </summary>
    public void SetTriggerTimestamp(DateTime timestamp)
    {
        TriggerTimestamp = timestamp;
    }

    /// <summary>
    ///     设置错误信息, 并将状态设置为 Error。
    /// </summary>
    public void SetError(string error)
    {
        ErrorMessage = error;
        // 将错误信息同时设置为StatusDisplay，这样在历史记录中能看到详细的错误描述
        SetStatus(PackageStatus.Error, error);
    }

    /// <summary>
    ///     设置体积。
    /// </summary>
    /// <param name="volume">体积（立方厘米）</param>
    public void SetVolume(double volume)
    {
        Volume = volume;
    }
}