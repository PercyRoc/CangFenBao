using System.Windows.Media.Imaging;

namespace Common.Models.Package;

/// <summary>
///     包裹分拣状态
/// </summary>
public enum PackageSortState
{
    /// <summary>
    ///     待处理
    /// </summary>
    Pending,

    /// <summary>
    ///     处理中
    /// </summary>
    Processing,

    /// <summary>
    ///     分拣超时（已废弃，现在使用Error状态）
    /// </summary>
    [Obsolete("已废弃，现在使用Error状态处理超时包裹")]
    TimedOut,

    /// <summary>
    ///     分拣错误（包裹超时错过分拣点，已从处理队列移除）
    /// </summary>
    Error,

    /// <summary>
    ///     已分拣
    /// </summary>
    Sorted
}

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
        { PackageStatus.Success, "分拣成功" },
        { PackageStatus.Failed, "分拣失败" },
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
        CreateTime = DateTime.Now;
        SortState = PackageSortState.Pending; // 初始化分拣状态
    }

    /// <summary>
    ///     创建 PackageInfo 的新实例并自动递增序号。
    /// </summary>
    /// <returns>新的 PackageInfo 实例。</returns>
    public static PackageInfo Create()
    {
        var newIndex = Interlocked.Increment(ref _currentIndex);
        var package = new PackageInfo
        {
            Index = newIndex
        };
        package.SetStatus(PackageStatus.Created);
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
    public string WeightDisplay
    {
        get => $"{Weight:F2}kg";
    }

    /// <summary>
    ///     体积显示
    /// </summary>
    public string VolumeDisplay
    {
        get => Length.HasValue && Width.HasValue && Height.HasValue
            ? $"{Length:F1}cm*{Width:F1}cm*{Height:F1}cm"
            : string.Empty;
    }

    /// <summary>
    ///     格口号
    /// </summary>
    public int ChuteNumber { get; private set; }

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
    public DateTime TriggerTimestamp { get; private set; }

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
    ///     托盘名称 (Sunnen项目专用)
    /// </summary>
    public string? PalletName { get; set; }

    /// <summary>
    ///     托盘重量，单位kg (Sunnen项目专用)
    /// </summary>
    public double PalletWeight { get; set; }

    /// <summary>
    ///     托盘长度，单位cm (Sunnen项目专用)
    /// </summary>
    public double PalletLength { get; set; }

    /// <summary>
    ///     托盘宽度，单位cm (Sunnen项目专用)
    /// </summary>
    public double PalletWidth { get; set; }

    /// <summary>
    ///     托盘高度，单位cm (Sunnen项目专用)
    /// </summary>
    public double PalletHeight { get; private set; }

    /// <summary>
    ///     分拣状态
    /// </summary>
    public PackageSortState SortState { get; private set; }

    /// <summary>
    ///     是否已上传到笨鸟系统
    /// </summary>
    public bool IsUploadedToBenNiao { get; private set; }

    /// <summary>
    ///     标记此包裹是否是前一个相同格口包裹的延续。
    ///     如果为 true，则意味着上一个包裹特意没有回正，为本包裹保留了摆轮状态。
    ///     这是"责任接力棒"机制的核心，用于解决连续相同格口包裹的状态传递问题。
    /// </summary>
    public bool IsChuteContinuation { get; set; }

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
    ///     设置体积。
    /// </summary>
    /// <param name="volume">体积（立方厘米）</param>
    public void SetVolume(double volume)
    {
        Volume = volume;
    }

    /// <summary>
    ///     设置托盘信息 (Sunnen项目专用)
    /// </summary>
    /// <param name="palletName">托盘名称</param>
    /// <param name="palletWeight">托盘重量(kg)</param>
    /// <param name="palletLength">托盘长度(cm)</param>
    /// <param name="palletWidth">托盘宽度(cm)</param>
    /// <param name="palletHeight">托盘高度(cm)</param>
    public void SetPallet(string? palletName, double palletWeight, double palletLength = 0, double palletWidth = 0, double palletHeight = 0)
    {
        PalletName = palletName;
        PalletWeight = palletWeight;
        PalletLength = palletLength;
        PalletWidth = palletWidth;
        PalletHeight = palletHeight;
    }

    /// <summary>
    ///     设置分拣状态
    /// </summary>
    /// <param name="sortState">分拣状态</param>
    public void SetSortState(PackageSortState sortState)
    {
        SortState = sortState;
    }

    /// <summary>
    ///     设置笨鸟上传状态
    /// </summary>
    /// <param name="isUploaded">是否已上传</param>
    public void SetBenNiaoUploadStatus(bool isUploaded)
    {
        IsUploadedToBenNiao = isUploaded;
    }
}