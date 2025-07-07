using System.Windows.Media.Imaging;

namespace Common.Models.Package;

/// <summary>
///     包裹信息
/// </summary>
public class PackageInfo : IDisposable
{
    private static int _currentIndex;
    private bool _disposed;

    /// <summary>
    ///     构造函数
    /// </summary>
    private PackageInfo()
    {
        CreateTime = DateTime.Now;
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
        package.SetStatus("Created");
        return package;
    }

    /// <summary>
    ///     序号
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    ///     唯一标识符 (来自TCP数据)
    /// </summary>
    public string Guid { get; set; } = string.Empty;

    /// <summary>
    ///     条码
    /// </summary>
    public string Barcode { get; set; } = string.Empty;

    /// <summary>
    ///     段码
    /// </summary>
    public string SegmentCode { get; set; } = string.Empty;

    /// <summary>
    ///     重量（千克）
    /// </summary>
    public double Weight { get; set; }

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
    public int ChuteNumber { get; set; }

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
    public string Status { get; set; } = string.Empty;

    /// <summary>
    ///     包裹计数
    /// </summary>
    public int PackageCount { get; set; }

    /// <summary>
    ///     额外数据字典，用于存储像 CameraId 这样的自定义数据。
    /// </summary>
    public Dictionary<string, object> AdditionalData { get; } = [];

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
    public double PalletHeight { get; set; }

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
    ///     设置处理状态。
    /// </summary>
    /// <param name="statusDisplay">状态的显示文本</param>
    public void SetStatus(string statusDisplay)
    {
        Status = statusDisplay;
        StatusDisplay = statusDisplay;
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
    ///     设置唯一标识符。
    /// </summary>
    /// <param name="guid">唯一标识符</param>
    public void SetGuid(string? guid)
    {
        Guid = guid ?? string.Empty;
    }

    /// <summary>
    ///     重新设置包裹的序号。
    /// </summary>
    /// <param name="newIndex">新的序号值。</param>
    public void SetIndex(int newIndex)
    {
        Index = newIndex;
    }

    /// <summary>
    /// 设置格口号。
    /// </summary>
    /// <param name="chuteNumber">格口号</param>
    public void SetChute(int chuteNumber)
    {
        ChuteNumber = chuteNumber;
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
}

/// <summary>
/// 格口状态项
/// </summary>
public class ChuteStatusItem : BindableBase
{
    private int _chuteNumber;
    private int _actualChuteNumber;
    private string _category = string.Empty;
    private string _description = string.Empty;
    private bool _isAssigned;
    private readonly List<string> _categories = [];

    /// <summary>
    /// 格口编号（显示用的配置格口号）
    /// </summary>
    public int ChuteNumber
    {
        get => _chuteNumber;
        set => SetProperty(ref _chuteNumber, value);
    }

    /// <summary>
    /// 实际格口编号（用于分拣的系统格口号）
    /// </summary>
    public int ActualChuteNumber
    {
        get => _actualChuteNumber;
        set => SetProperty(ref _actualChuteNumber, value);
    }

    /// <summary>
    /// 分配的类别（显示第一个类别，如果有多个则显示"多类别"）
    /// </summary>
    public string Category
    {
        get => _category;
        set => SetProperty(ref _category, value);
    }

    /// <summary>
    /// 所有分配的类别列表
    /// </summary>
    public List<string> Categories => _categories;

    /// <summary>
    /// 描述信息
    /// </summary>
    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    /// <summary>
    /// 是否已分配
    /// </summary>
    public bool IsAssigned
    {
        get => _isAssigned;
        set => SetProperty(ref _isAssigned, value);
    }

    /// <summary>
    /// 显示文本（用于UI绑定）
    /// </summary>
    public string DisplayText => _categories.Count == 0 ? "空闲" : 
                                _categories.Count == 1 ? _categories[0] : 
                                $"多区域({_categories.Count})";

    public ChuteStatusItem(int chuteNumber)
    {
        ChuteNumber = chuteNumber;
        ActualChuteNumber = 2 * chuteNumber - 1; // 新的映射关系: 1->1, 2->3, 3->5
        IsAssigned = false;
        Description = "空闲";
        // 触发初始化时的属性变更通知
        RaisePropertyChanged(nameof(DisplayText));
    }

    /// <summary>
    /// 构造函数，指定显示格口号和实际格口号
    /// </summary>
    /// <param name="displayChuteNumber">显示的格口号</param>
    /// <param name="actualChuteNumber">实际的格口号</param>
    public ChuteStatusItem(int displayChuteNumber, int actualChuteNumber)
    {
        ChuteNumber = displayChuteNumber;
        ActualChuteNumber = actualChuteNumber;
        IsAssigned = false;
        Description = "空闲";
        // 触发初始化时的属性变更通知
        RaisePropertyChanged(nameof(DisplayText));
    }

    /// <summary>
    /// 分配单个类别
    /// </summary>
    /// <param name="category">类别名称</param>
    public void AssignCategory(string category)
    {
        if (!_categories.Contains(category))
        {
            _categories.Add(category);
            UpdateDisplayInfo();
        }
    }

    /// <summary>
    /// 移除指定类别
    /// </summary>
    /// <param name="category">要移除的类别</param>
    public void RemoveCategory(string category)
    {
        if (_categories.Remove(category))
        {
            UpdateDisplayInfo();
        }
    }

    /// <summary>
    /// 清空所有分配
    /// </summary>
    public void Clear()
    {
        _categories.Clear();
        Category = string.Empty;
        IsAssigned = false;
        Description = "空闲";
    }

    /// <summary>
    /// 更新显示信息
    /// </summary>
    private void UpdateDisplayInfo()
    {
        if (_categories.Count == 0)
        {
            Category = string.Empty;
            IsAssigned = false;
            Description = "空闲";
        }
        else if (_categories.Count == 1)
        {
            Category = _categories[0];
            IsAssigned = true;
            Description = $"已分配给大区: {_categories[0]}";
        }
        else
        {
            Category = $"多大区({_categories.Count})";
            IsAssigned = true;
            Description = $"已分配给 {_categories.Count} 个大区: {string.Join(", ", _categories)}";
        }
        
        // 通知DisplayText属性变更
        RaisePropertyChanged(nameof(DisplayText));
    }
}