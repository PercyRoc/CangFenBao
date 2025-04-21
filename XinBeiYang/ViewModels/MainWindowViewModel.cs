using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Common.Models.Package;
using Common.Services.Audio;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Weight;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Services.Dialogs;
using Serilog;
using SharedUI.Models;
using XinBeiYang.Models;
using XinBeiYang.Models.Communication;
using XinBeiYang.Services;
using System.Collections.Concurrent;

// Added for CancellationTokenSource

namespace XinBeiYang.ViewModels;

#region 条码模式枚举

/// <summary>
/// 条码模式枚举
/// </summary>
public enum BarcodeMode
{
    /// <summary>
    /// 多条码模式（合并处理）
    /// </summary>
    MultiBarcode,

    /// <summary>
    /// 仅处理母条码（符合正则表达式的条码）
    /// </summary>
    ParentBarcode,

    /// <summary>
    /// 仅处理子条码（不符合正则表达式的条码）
    /// </summary>
    ChildBarcode
}

#endregion

#region 设备状态信息类

/// <summary>
/// 设备状态信息类
/// </summary>
public class DeviceStatusInfo(string name, string icon, string status, string statusColor) : BindableBase
{
    private string _name = name;
    private string _icon = icon;
    private string _status = status;
    private string _statusColor = statusColor;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Icon
    {
        get => _icon;
        set => SetProperty(ref _icon, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string StatusColor
    {
        get => _statusColor;
        set => SetProperty(ref _statusColor, value);
    }
}

#endregion

#region 相机展示信息类

/// <summary>
/// 相机展示信息类
/// </summary>
public class CameraDisplayInfo : BindableBase
{
    private readonly string _cameraId = string.Empty;
    private readonly string _cameraName = string.Empty;
    private bool _isOnline;
    private BitmapSource? _currentImage;
    private int _row;
    private int _column;
    private int _rowSpan = 1;
    private int _columnSpan = 1;

    /// <summary>
    /// 相机ID
    /// </summary>
    public string CameraId
    {
        get => _cameraId;
        init => SetProperty(ref _cameraId, value);
    }

    /// <summary>
    /// 相机名称
    /// </summary>
    public string CameraName
    {
        get => _cameraName;
        init => SetProperty(ref _cameraName, value);
    }

    /// <summary>
    /// 相机是否在线
    /// </summary>
    public bool IsOnline
    {
        get => _isOnline;
        set => SetProperty(ref _isOnline, value);
    }

    /// <summary>
    /// 当前图像
    /// </summary>
    public BitmapSource? CurrentImage
    {
        get => _currentImage;
        set => SetProperty(ref _currentImage, value);
    }

    /// <summary>
    /// 在网格中的行位置
    /// </summary>
    public int Row
    {
        get => _row;
        set => SetProperty(ref _row, value);
    }

    /// <summary>
    /// 在网格中的列位置
    /// </summary>
    public int Column
    {
        get => _column;
        set => SetProperty(ref _column, value);
    }

    /// <summary>
    /// 占用的行数
    /// </summary>
    public int RowSpan
    {
        get => _rowSpan;
        set => SetProperty(ref _rowSpan, value);
    }

    /// <summary>
    /// 占用的列数
    /// </summary>
    public int ColumnSpan
    {
        get => _columnSpan;
        set => SetProperty(ref _columnSpan, value);
    }
}

#endregion

/// <summary>
/// 主窗口视图模型
/// </summary>
internal partial class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly ICameraService _cameraService;
    private readonly IDialogService _dialogService;
    private readonly IAudioService _audioService;
    private readonly IPlcCommunicationService _plcCommunicationService;
    private readonly IJdWcsCommunicationService _jdWcsCommunicationService;
    private readonly IImageStorageService _imageStorageService;
    private readonly ISettingsService _settingsService;
    private readonly SerialPortWeightService _weightService; // 添加重量称服务依赖
    private readonly List<IDisposable> _subscriptions = [];
    private readonly DispatcherTimer _timer;
    private string _currentBarcode = string.Empty;
    private BitmapSource? _currentImage;
    private bool _disposed;
    private SystemStatus _systemStatus = new();
    private string _deviceStatusText = "未连接";
    private string _deviceStatusDescription = "PLC设备未连接，请检查网络连接";
    private string _deviceStatusColor = "#F44336";
    private string _jdStatusText = "未连接";
    private string _jdStatusDescription = "京东WCS服务未连接，请检查网络连接";
    private string _jdStatusColor = "#F44336";
    private bool _lastPackageWasSuccessful = true; // 初始状态为成功
    private Brush _mainWindowBackgroundBrush = BackgroundSuccess; // Start with Green (Allow loading)
    private bool _isPlcRejectWarningVisible; // PLC拒绝警告可见性标志
    private bool _isPlcAbnormalWarningVisible; // PLC异常警告可见性标志
    private CancellationTokenSource? _rejectionWarningCts;
    private BarcodeMode _barcodeMode = BarcodeMode.MultiBarcode; // 默认为多条码模式
    private int _selectedBarcodeModeIndex; // 新增：用于绑定 ComboBox 的 SelectedIndex

    // 新增：相机初始化标志
    private bool _camerasInitialized;

    private int _viewModelPackageIndex;

    // *** 新增: 用于存储最近超时的条码前缀 ***
    private readonly ConcurrentDictionary<string, DateTime> _timedOutPrefixes = new();

    // 超时条目在缓存中的最大保留时间
    private static readonly TimeSpan TimedOutPrefixMaxAge = TimeSpan.FromSeconds(15);

    // 母条码正则表达式
    private static readonly Regex ParentBarcodeRegex = MyRegex();

    // Define new Brush constants (or create them inline)
    private static readonly Brush
        BackgroundSuccess =
            new SolidColorBrush(Color.FromArgb(0xAA, 0x4C, 0xAF, 0x50)); // 绿色 (增加透明度) - Used for Allow Loading

    private static readonly Brush
        BackgroundTimeout =
            new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xC1, 0x07)); // 黄色 (增加透明度) - Used for Prohibit Loading

    private static readonly Brush
        BackgroundRejected =
            new SolidColorBrush(Color.FromArgb(0xAA, 0xF4, 0x43, 0x36)); // 红色 (增加透明度) - Used for Rejected

    // *** ADDED: State Management for Sequential Processing ***
    private PackageInfo? _currentlyProcessingPackage;
    private PackageInfo? _pendingPackage;
    private readonly object _processingLock = new();
    private readonly CancellationTokenSource _viewModelCts = new(); // 视图模型的主要取消标记

    private bool _isNextPackageWaiting; // 用于UI指示 (现在指示 _pendingpackage是否不为null)

    /// <summary>
    /// 指示是否有包裹正在队列中等待PLC处理
    /// </summary>
    public bool IsNextPackageWaiting
    {
        get => _isNextPackageWaiting;
        private set => SetProperty(ref _isNextPackageWaiting, value);
    }

    public MainWindowViewModel(
        IDialogService dialogService,
        ICameraService cameraService,
        IAudioService audioService,
        IPlcCommunicationService plcCommunicationService,
        IJdWcsCommunicationService jdWcsCommunicationService,
        IImageStorageService imageStorageService,
        ISettingsService settingsService,
        SerialPortWeightService weightService) // 添加重量称服务参数
    {
        _dialogService = dialogService;
        _cameraService = cameraService;
        _audioService = audioService;
        _plcCommunicationService = plcCommunicationService;
        _jdWcsCommunicationService = jdWcsCommunicationService;
        _imageStorageService = imageStorageService;
        _settingsService = settingsService;
        _weightService = weightService; // 初始化重量称服务

        // --- 初始化 ---
        OpenSettingsCommand = new DelegateCommand(ExecuteOpenSettings);

        // 加载保存的条码模式配置
        LoadBarcodeModeFromSettings();
        // 初始化 SelectedIndex
        _selectedBarcodeModeIndex = (int)_barcodeMode;

        // 初始化系统状态更新定时器
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        // 初始化设备状态
        InitializeDeviceStatuses();

        // 初始化相机视图（添加加载中占位符）
        InitializeCameraPlaceholder();

        // 初始化统计数据
        InitializeStatisticsItems();

        // 初始化包裹信息
        InitializePackageInfoItems();

        // 订阅相机连接状态事件
        _cameraService.ConnectionChanged += OnCameraConnectionChanged;

        // 订阅PLC设备状态变更事件 - 统一处理连接和状态变更
        _plcCommunicationService.DeviceStatusChanged += OnPlcDeviceStatusChanged;

        // 订阅京东WCS连接状态变更事件
        _jdWcsCommunicationService.ConnectionChanged += OnJdWcsConnectionChanged;

        // --- 包裹流处理 (重构) ---
        _subscriptions.Add(_cameraService.PackageStream
            .ObserveOn(Scheduler.Default) // GroupBy and Buffer can run on a background thread
            .Do(pkg => Log.Debug("[Stream] 包裹通过过滤器: Barcode={Barcode}, Index={Index}, Timestamp={Timestamp}",
                pkg.Barcode, pkg.Index, DateTime.Now.ToString("O"))) // <-- 日志点 1: 过滤后
            .Where(FilterBarcodeByMode) // 根据当前条码模式过滤包裹
            .GroupBy(p =>
            {
                var prefix = GetBarcodePrefix(p.Barcode);
                Log.Debug("[Stream] 创建或加入分组: Prefix={Prefix}, Barcode={Barcode}, Index={Index}", prefix, p.Barcode,
                    p.Index); // <-- 日志点 2: 分组时
                return prefix;
            })
            .SelectMany(group => group
                // Buffer for a short time (e.g., 500ms) or until 2 items arrive
                .Buffer(TimeSpan.FromMilliseconds(500), 2)
                // If Buffer emits an empty list, filter it out.
                .Where(buffer => buffer.Count > 0)
            )
            // *** Observe on a background thread for merging/handling ***
            .ObserveOn(Scheduler.Default)
            .Subscribe(buffer => // *** Make lambda async ***
            {
                var currentTimestamp = DateTime.Now.ToString("O"); // 获取当前时间戳
                var firstPackage = buffer[0]; // Get the first package to check its prefix
                var currentPrefix = GetBarcodePrefix(firstPackage.Barcode);

                // *** 1. 检查此前是否已记录该前缀超时 ***
                if (_timedOutPrefixes.TryGetValue(currentPrefix, out var timeoutTime))
                {
                    // 检查超时记录是否在有效期内
                    if ((DateTime.UtcNow - timeoutTime) < TimedOutPrefixMaxAge)
                    {
                        // 如果当前只收到一个包裹，这很可能就是那个"迟到"的配对包裹
                        if (buffer.Count == 1)
                        {
                            Log.Warning("[Stream] 丢弃迟到的包裹 (配对已超时): Prefix={Prefix}, Barcode={Barcode}, Index={Index}",
                                currentPrefix, firstPackage.Barcode, firstPackage.Index);
                            firstPackage.ReleaseImage();
                            // 既然迟到的包裹已收到并丢弃，移除超时记录
                            _timedOutPrefixes.TryRemove(currentPrefix, out _);
                            return; // 不再处理此包裹
                        }

                        // 如果收到了两个包裹，但此前记录了超时？这不太可能发生，但作为健壮性处理
                        // 我们假设配对最终成功了，移除超时标记并继续处理
                        Log.Warning("[Stream] 收到配对包裹，但此前记录了前缀超时。继续处理并移除超时标记: Prefix={Prefix}", currentPrefix);
                        _timedOutPrefixes.TryRemove(currentPrefix, out _);
                    }
                    else
                    {
                        // 超时记录已过期，移除它
                        _timedOutPrefixes.TryRemove(currentPrefix, out _);
                        Log.Debug("[State] 清理过期的超时前缀记录: {Prefix}", currentPrefix);
                    }
                }

                // *** 2. 正常处理逻辑 ***
                PackageInfo packageToProcess;

                switch (buffer.Count)
                {
                    // 多条码模式下才尝试合并
                    case 2 when BarcodeMode == BarcodeMode.MultiBarcode:
                    {
                        // Ensure consistent order if needed (e.g., non-suffix first)
                        var p1 = buffer.FirstOrDefault(p => !p.Barcode.EndsWith("-1-1-")) ?? buffer[0];
                        var p2 = buffer.FirstOrDefault(p => p.Barcode.EndsWith("-1-1-")) ?? buffer[1];
                        var prefix = GetBarcodePrefix(p1.Barcode);
                        // <-- 日志点 4: 成功配对
                        Log.Information(
                            "[Stream] 成功配对 (Timestamp: {Timestamp}): Prefix={Prefix}, Pkg1='{Barcode1}' (Index:{Index1}), Pkg2='{Barcode2}' (Index:{Index2})",
                            currentTimestamp, prefix, p1.Barcode, p1.Index, p2.Barcode, p2.Index);
                        Log.Information("收到成对包裹，前缀 {Prefix}。准备合并: Index1={Index1}, Index2={Index2}", prefix, p1.Index,
                            p2.Index);
                        packageToProcess = MergePackageInfo(p1, p2);
                        // Release images from original packages after merging
                        p1.ReleaseImage();
                        p2.ReleaseImage();
                        Log.Information("包裹合并完成: Index={MergedIndex}, Barcode='{MergedBarcode}'",
                            packageToProcess.Index, packageToProcess.Barcode);
                        // 清除可能存在的超时标记
                        _timedOutPrefixes.TryRemove(prefix, out _);
                        break;
                    }
                    // 收到两个包裹，但模式不是 MultiBarcode
                    case 2:
                    {
                        // 根据当前 ParentBarcode 或 ChildBarcode 模式选择正确的包裹
                        PackageInfo packageToKeep;
                        PackageInfo packageToDiscard;
                        var p1IsParent = ParentBarcodeRegex.IsMatch(buffer[0].Barcode);

                        if (BarcodeMode == BarcodeMode.ParentBarcode)
                        {
                            packageToKeep = p1IsParent ? buffer[0] : buffer[1];
                            packageToDiscard = p1IsParent ? buffer[1] : buffer[0];
                            Log.Information(
                                "非多条码模式(母条码): 收到两个包裹，选择符合规则的母条码: {Barcode} (序号: {Index})，丢弃: {DiscardedBarcode}",
                                packageToKeep.Barcode, packageToKeep.Index, packageToDiscard.Barcode);
                        }
                        else // BarcodeMode == BarcodeMode.ChildBarcode
                        {
                            packageToKeep = !p1IsParent ? buffer[0] : buffer[1];
                            packageToDiscard = !p1IsParent ? buffer[1] : buffer[0];
                            Log.Information(
                                "非多条码模式(子条码): 收到两个包裹，选择符合规则的子条码: {Barcode} (序号: {Index})，丢弃: {DiscardedBarcode}",
                                packageToKeep.Barcode, packageToKeep.Index, packageToDiscard.Barcode);
                        }

                        packageToProcess = packageToKeep;
                        packageToDiscard.ReleaseImage(); // 释放被丢弃包裹的图像
                        break;
                    }
                    // buffer.Count == 1 (包括 MultiBarcode 超时 或 Parent/Child 正常单个包裹)
                    default:
                    {
                        packageToProcess = firstPackage; // 就是我们开始检查的那个
                        if (BarcodeMode == BarcodeMode.MultiBarcode) // MultiBarcode 模式下的超时
                        {
                            // var expectedPrefix = GetBarcodePrefix(packageToProcess.Barcode); // currentPrefix 已获取
                            Log.Warning(
                                "[Stream] 配对超时 (Timestamp: {Timestamp}): Prefix={Prefix}, ArrivedBarcode=\'{Barcode}\' (Index:{Index}). 将单独处理.",
                                currentTimestamp, currentPrefix, packageToProcess.Barcode, packageToProcess.Index);

                            // *** 记录此次超时 ***
                            Log.Information("[State] 记录配对超时前缀: {Prefix}", currentPrefix);
                            _timedOutPrefixes[currentPrefix] = DateTime.UtcNow; // 记录或更新超时时间
                            CleanupTimedOutPrefixes(TimedOutPrefixMaxAge); // 清理过期条目
                        }
                        else // Parent/Child 模式下的单个包裹 (已经被 Where 操作符正确过滤)
                        {
                            Log.Information("{Mode}模式下处理单个包裹: {Barcode} (序号: {Index})",
                                GetBarcodeModeDisplayText(BarcodeMode), packageToProcess.Barcode,
                                packageToProcess.Index);
                        }

                        break;
                    }
                }

                // *** 3. 调用新的处理入口 ***
                try
                {
                    // *** Assign ViewModel index BEFORE passing to handler ***
                    var assignedIndex = Interlocked.Increment(ref _viewModelPackageIndex);
                    packageToProcess.Index = assignedIndex;
                    Log.Information("[接收] 包裹进入处理流程，分配序号: {ViewModelIndex}", assignedIndex);

                    // *** Call the new handler method ***
                    HandleIncomingPackage(packageToProcess);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理合并后/单个包裹时发生错误: {Barcode}", packageToProcess.Barcode);
                    // Clean up the package if processing failed early
                    packageToProcess.ReleaseImage();
                }
            }, ex => Log.Error(ex, "包裹流处理中发生未处理异常"))); // Add overall error handling for the stream

        // +++ 订阅来自 ICameraService 的带 ID 图像流 +++
        _subscriptions.Add(_cameraService.ImageStreamWithId
            .ObserveOn(Scheduler.Default) // 在UI线程外处理转换，但WPF对象需要UI线程访问
            .Subscribe(imageData =>
            {
                var cameraId = imageData.CameraId; // 获取相机ID
                var image = imageData.Image; // 获取图像
                try
                {
                    // 仅为UI更新切换到UI线程
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        // 在UI线程上查找目标相机
                        var targetCamera = Cameras.FirstOrDefault(c => c.CameraId == cameraId);
                        if (targetCamera == null)
                        {
                            Log.Warning("未找到相机ID={CameraId}的显示区域", cameraId);
                            return;
                        }

                        try
                        {
                            // 使用本地副本更新UI属性，而不是外部变量
                            targetCamera.CurrentImage = image;
                        }
                        catch (Exception innerEx) // 捕获UI更新期间的潜在错误
                        {
                            Log.Error(innerEx, "在UI线程上更新UI时出错: CameraId={CameraId}", cameraId);
                        }
                    }); // 结束Dispatcher.Invoke
                }
                catch (Exception ex) // 捕获后台处理期间的错误
                {
                    Log.Error(ex, "处理图像时发生错误: CameraId={CameraId}, Message={Message}", cameraId, ex.Message);
                }
            }));

        // Set initial background to Green
        MainWindowBackgroundBrush = BackgroundSuccess;

        // 检查相机服务是否已经连接，如果是则立即填充相机列表
        if (!_cameraService.IsConnected || _camerasInitialized) return;
        Log.Information("相机服务已连接，立即填充相机列表");
        PopulateCameraList();
    }

    public DelegateCommand OpenSettingsCommand { get; }
    public ObservableCollection<PackageInfo> PackageHistory { get; } = [];
    public ObservableCollection<StatisticsItem> StatisticsItems { get; } = [];
    public ObservableCollection<DeviceStatusInfo> DeviceStatuses { get; private set; } = [];
    public ObservableCollection<PackageInfoItem> PackageInfoItems { get; } = [];

    public ObservableCollection<CameraDisplayInfo> Cameras { get; } = [];

    public string DeviceStatusText
    {
        get => _deviceStatusText;
        private set => SetProperty(ref _deviceStatusText, value);
    }

    public string DeviceStatusDescription
    {
        get => _deviceStatusDescription;
        private set => SetProperty(ref _deviceStatusDescription, value);
    }

    public string DeviceStatusColor
    {
        get => _deviceStatusColor;
        set => SetProperty(ref _deviceStatusColor, value);
    }

    public BitmapSource? CurrentImage
    {
        get => _currentImage;
        set => SetProperty(ref _currentImage, value);
    }

    public SystemStatus SystemStatus
    {
        get => _systemStatus;
        private set => SetProperty(ref _systemStatus, value);
    }

    public string CurrentBarcode
    {
        get => _currentBarcode;
        private set => SetProperty(ref _currentBarcode, value);
    }

    public string JdStatusText
    {
        get => _jdStatusText;
        private set => SetProperty(ref _jdStatusText, value);
    }

    public string JdStatusDescription
    {
        get => _jdStatusDescription;
        private set => SetProperty(ref _jdStatusDescription, value);
    }

    public string JdStatusColor
    {
        get => _jdStatusColor;
        set => SetProperty(ref _jdStatusColor, value);
    }

    /// <summary>
    /// 指示上一个处理的包裹是否成功
    /// </summary>
    public bool LastPackageWasSuccessful
    {
        get => _lastPackageWasSuccessful;
        private set => SetProperty(ref _lastPackageWasSuccessful, value);
    }

    /// <summary>
    /// 主窗口内容区域背景画刷 - 控制上包状态 (黄/绿)
    /// </summary>
    public Brush MainWindowBackgroundBrush
    {
        get => _mainWindowBackgroundBrush;
        private set => SetProperty(ref _mainWindowBackgroundBrush, value);
    }

    /// <summary>
    /// 控制PLC拒绝警告覆盖层的可见性
    /// </summary>
    public bool IsPlcRejectWarningVisible
    {
        get => _isPlcRejectWarningVisible;
        private set => SetProperty(ref _isPlcRejectWarningVisible, value);
    }

    /// <summary>
    /// 控制PLC异常警告覆盖层的可见性
    /// </summary>
    public bool IsPlcAbnormalWarningVisible
    {
        get => _isPlcAbnormalWarningVisible;
        private set => SetProperty(ref _isPlcAbnormalWarningVisible, value);
    }

    /// <summary>
    /// 条码模式
    /// </summary>
    private BarcodeMode BarcodeMode
    {
        get => _barcodeMode;
        set
        {
            if (!SetProperty(ref _barcodeMode, value)) return;
            Log.Information("条码模式已更改为: {Mode}", GetBarcodeModeDisplayText(value));

            // 保存条码模式到配置
            SaveBarcodeModeToSettings(value);
            // 更新 SelectedIndex 属性以同步 UI
            // 注意：这里不需要再次调用 SetProperty，因为它会在 SelectedBarcodeModeIndex 的 setter 中被调用
            _selectedBarcodeModeIndex = (int)value;
            RaisePropertyChanged(nameof(SelectedBarcodeModeIndex)); // 手动通知 SelectedIndex 更改
        }
    }

    /// <summary>
    /// 用于绑定 ComboBox.SelectedIndex 的属性
    /// </summary>
    public int SelectedBarcodeModeIndex
    {
        get => _selectedBarcodeModeIndex;
        set
        {
            // 使用 SetProperty 检查值是否真的改变
            if (SetProperty(ref _selectedBarcodeModeIndex, value))
            {
                // 当 Index 改变时，更新 BarcodeMode 枚举属性
                // BarcodeMode 的 setter 会处理日志记录和保存设置
                BarcodeMode = (BarcodeMode)value;
            }
        }
    }

    /// <summary>
    /// 获取条码模式的显示文本
    /// </summary>
    private static string GetBarcodeModeDisplayText(BarcodeMode mode)
    {
        return mode switch
        {
            BarcodeMode.MultiBarcode => "多条码",
            BarcodeMode.ParentBarcode => "母条码",
            BarcodeMode.ChildBarcode => "子条码",
            _ => "未知模式"
        };
    }

    /// <summary>
    /// 根据当前设置的条码模式过滤包裹
    /// </summary>
    private bool FilterBarcodeByMode(PackageInfo package)
    {
        // 检查是否有效条码
        if (string.IsNullOrEmpty(package.Barcode) ||
            string.Equals(package.Barcode, "noread", StringComparison.OrdinalIgnoreCase))
        {
            Log.Debug("[Filter] 包裹因条码无效或为 'noread' 被过滤: {Barcode}", package.Barcode);
            return false;
        }

        var isParentBarcode = ParentBarcodeRegex.IsMatch(package.Barcode);

        var pass = BarcodeMode switch
        {
            BarcodeMode.MultiBarcode => true, // 多条码模式不过滤
            BarcodeMode.ParentBarcode => isParentBarcode, // 母条码模式只处理符合正则的条码
            BarcodeMode.ChildBarcode => !isParentBarcode, // 子条码模式只处理不符合正则的条码
            _ => true
        };

        if (!pass)
        {
            Log.Debug("[Filter] 包裹因模式 {Mode} 与条码类型 (IsParent: {IsParent}) 不符被过滤: {Barcode}",
                BarcodeMode, isParentBarcode, package.Barcode);
        }

        return pass;
    }

    /// <summary>
    /// 初始化相机占位符
    /// </summary>
    private void InitializeCameraPlaceholder()
    {
        Log.Information("初始化相机视图 (添加加载中占位符)...");
        try
        {
            if (Cameras.Count != 0) return;
            CalculateOptimalLayout(1); // 1个相机的布局
            var (r, c, rs, cs) = GetCameraPosition(0, 1);
            Cameras.Add(new CameraDisplayInfo
            {
                CameraId = "Loading",
                CameraName = "正在加载相机...",
                IsOnline = false,
                Row = r,
                Column = c,
                RowSpan = rs,
                ColumnSpan = cs
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化相机占位符时发生错误");
        }
    }

    /// <summary>
    /// 填充相机列表（在相机服务连接成功后调用）
    /// </summary>
    private void PopulateCameraList()
    {
        if (_camerasInitialized) return; // 防止重复执行

        Log.Information("开始填充相机列表 (服务已连接)...");
        try
        {
            Cameras.Clear(); // 清空现有列表（包括占位符）

            // 直接调用接口方法获取相机列表
            var availableCameras = _cameraService.GetAvailableCameras().ToList();
            var cameraCount = availableCameras.Count;
            Log.Information("获取到 {Count} 个相机 (通过 ICameraService)", cameraCount);

            if (cameraCount > 0)
            {
                CalculateOptimalLayout(cameraCount);
                for (var i = 0; i < cameraCount; i++)
                {
                    var cameraInfo = availableCameras[i];
                    var (row, column, rowSpan, columnSpan) = GetCameraPosition(i, cameraCount);

                    Log.Information("添加相机视图: ID={ID}, 名称={Name}, 行={Row}, 列={Column}",
                        cameraInfo.Id, cameraInfo.Name, row, column);

                    var displayInfo = new CameraDisplayInfo
                    {
                        CameraId = cameraInfo.Id, // 使用从接口获取的ID
                        CameraName = cameraInfo.Name, // 使用从接口获取的Name
                        IsOnline = true, // 初始状态设为在线（服务已连接）
                        Row = row,
                        Column = column,
                        RowSpan = rowSpan,
                        ColumnSpan = columnSpan,
                    };
                    Cameras.Add(displayInfo);
                }
                // 标记已初始化
            }
            else // 未获取到相机
            {
                Log.Warning("相机服务已连接，但未获取到相机信息，添加默认相机占位符");
                CalculateOptimalLayout(1); // 1个相机的布局
                var (r, c, rs, cs) = GetCameraPosition(0, 1);
                Cameras.Add(new CameraDisplayInfo
                {
                    CameraId = "Placeholder_0",
                    CameraName = "主相机（未检测到）",
                    IsOnline = false,
                    Row = r,
                    Column = c,
                    RowSpan = rs,
                    ColumnSpan = cs
                });
                // 标记已初始化（即使是占位符）
            }

            _camerasInitialized = true; // 标记已初始化
        }
        catch (Exception ex)
        {
            Log.Error(ex, "填充相机列表时发生错误");
            // 发生错误也尝试添加占位符，防止UI空白
            if (Cameras.Count == 0)
            {
                CalculateOptimalLayout(1);
                var (r, c, rs, cs) = GetCameraPosition(0, 1);
                Cameras.Add(new CameraDisplayInfo
                {
                    CameraId = "Error_Placeholder",
                    CameraName = "相机加载错误",
                    IsOnline = false,
                    Row = r,
                    Column = c,
                    RowSpan = rs,
                    ColumnSpan = cs
                });
            }
        }
    }

    private void OnCameraConnectionChanged(string? cameraId, bool isConnected)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var cameraStatus = DeviceStatuses.FirstOrDefault(static s => s.Name == "相机");
            if (cameraStatus != null)
            {
                cameraStatus.Status = isConnected ? "已连接" : "已断开";
                cameraStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
            }

            // 首次整体连接成功时，填充相机列表
            if (string.IsNullOrEmpty(cameraId) && isConnected && !_camerasInitialized)
            {
                Log.Information("相机服务整体连接成功，开始填充相机列表");
                PopulateCameraList();
                return;
            }

            // 处理相机断开连接
            if (!isConnected && string.IsNullOrEmpty(cameraId))
            {
                foreach (var cam in Cameras)
                {
                    cam.IsOnline = false;
                }

                return;
            }

            // 更新单个相机状态
            if (string.IsNullOrEmpty(cameraId)) return;
            {
                foreach (var cam in Cameras)
                {
                    if (cam.CameraId != cameraId) continue;
                    cam.IsOnline = isConnected;
                    Log.Information("更新相机 {ID} 状态为: {Status}", cameraId, isConnected ? "在线" : "离线");
                    break;
                }
            }
        });
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void ExecuteOpenSettings()
    {
        _dialogService.ShowDialog("SettingsDialog");
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        SystemStatus = SystemStatus.GetCurrentStatus();
    }

    private void InitializeDeviceStatuses()
    {
        DeviceStatuses =
        [
            new DeviceStatusInfo("相机", "Camera24", _cameraService.IsConnected ? "已连接" : "已断开",
                _cameraService.IsConnected ? "#4CAF50" : "#F44336"),
            new DeviceStatusInfo("PLC", "Router24", _plcCommunicationService.IsConnected ? "正常" : "未连接",
                _plcCommunicationService.IsConnected ? "#4CAF50" : "#F44336"), // Added PLC status
            new DeviceStatusInfo("京东WCS", "Cloud24", _jdWcsCommunicationService.IsConnected ? "已连接" : "未连接",
                _jdWcsCommunicationService.IsConnected ? "#4CAF50" : "#F44336") // Added WCS status
        ];
        // Initial update for combined PLC status text
        OnPlcDeviceStatusChanged(this,
            _plcCommunicationService.IsConnected ? DeviceStatusCode.Normal : DeviceStatusCode.Disconnected);
        // Initial update for WCS status text
        OnJdWcsConnectionChanged(this, _jdWcsCommunicationService.IsConnected);
    }

    private void InitializeStatisticsItems()
    {
        StatisticsItems.Add(new StatisticsItem
        {
            Label = "总包裹数",
            Value = "0",
            Unit = "个",
            Description = "累计处理包裹总数",
            Icon = "BoxMultiple24"
        });

        StatisticsItems.Add(new StatisticsItem
        {
            Label = "成功数",
            Value = "0",
            Unit = "个",
            Description = "处理成功的包裹数量",
            Icon = "CheckmarkCircle24"
        });

        StatisticsItems.Add(new StatisticsItem
        {
            Label = "失败数",
            Value = "0",
            Unit = "个",
            Description = "处理失败的包裹数量",
            Icon = "ErrorCircle24"
        });

        StatisticsItems.Add(new StatisticsItem
        {
            Label = "处理速率",
            Value = "0",
            Unit = "个/小时",
            Description = "每小时处理包裹数量",
            Icon = "ArrowTrendingLines24"
        });
    }

    private void InitializePackageInfoItems()
    {
        PackageInfoItems.Add(new PackageInfoItem
        {
            Label = "重量",
            Value = "0.00",
            Unit = "kg",
            Description = "包裹重量",
            Icon = "Scales24"
        });

        PackageInfoItems.Add(new PackageInfoItem
        {
            Label = "尺寸",
            Value = "0 × 0 × 0",
            Unit = "mm",
            Description = "长 × 宽 × 高",
            Icon = "Ruler24"
        });

        PackageInfoItems.Add(new PackageInfoItem
        {
            Label = "时间",
            Value = "--:--:--",
            Description = "处理时间",
            Icon = "Timer24"
        });

        PackageInfoItems.Add(new PackageInfoItem
        {
            Label = "状态",
            Value = "等待扫码",
            Description = "等待 PLC 指令或扫码",
            Icon = "Alert24"
        });
    }

    private void OnJdWcsConnectionChanged(object? sender, bool isConnected)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            try
            {
                // 更新设备状态列表中的京东WCS状态
                var jdWcsStatus = DeviceStatuses.FirstOrDefault(static s => s.Name == "京东WCS");
                if (jdWcsStatus != null)
                {
                    jdWcsStatus.Status = isConnected ? "已连接" : "已断开";
                    jdWcsStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
                }

                // 更新京东WCS状态显示
                JdStatusText = isConnected ? "已连接" : "未连接";
                JdStatusDescription = isConnected
                    ? "京东WCS服务连接正常，可以上传图片"
                    : "京东WCS服务未连接，请检查网络连接";
                JdStatusColor = isConnected ? "#4CAF50" : "#F44336";
            }
            catch (Exception ex)
            {
                Log.Error(ex, "更新京东WCS状态时发生错误");
            }
        });
    }

    // *** ADDED: New entry point for handling incoming packages ***
    private void HandleIncomingPackage(PackageInfo package)
    {
        // Use a lock to safely access and modify shared state (_currentlyProcessingPackage, _pendingPackage)
        lock (_processingLock)
        {
            if (_currentlyProcessingPackage == null)
            {
                // No package is currently being processed, start processing this one
                _currentlyProcessingPackage = package;
                Log.Information("[调度] 无当前处理包裹，开始处理新包裹: {Barcode}(序号:{Index})", package.Barcode, package.Index);
                // Start processing asynchronously, do not await here
                _ = ProcessSinglePackageAsync(_currentlyProcessingPackage, _viewModelCts.Token);
            }
            else
            {
                // A package is already being processed, cache this new package
                Log.Warning("[调度] 当前正在处理包裹 {CurrentBarcode}(序号:{CurrentIndex})，缓存新包裹: {NewBarcode}(序号:{NewIndex})",
                    _currentlyProcessingPackage.Barcode, _currentlyProcessingPackage.Index,
                    package.Barcode, package.Index);

                // Discard the previous pending package if it exists
                if (_pendingPackage != null)
                {
                    Log.Warning("[调度] 发现已有待处理包裹 {OldPendingBarcode}(序号:{OldPendingIndex})，将被丢弃",
                        _pendingPackage.Barcode, _pendingPackage.Index);
                    _pendingPackage.ReleaseImage();
                }

                _pendingPackage = package;
                IsNextPackageWaiting = true; // Update UI indicator

                // Set background to Yellow immediately as a new package arrived while busy
                Application.Current.Dispatcher.Invoke(() =>
                {
                    MainWindowBackgroundBrush = BackgroundTimeout; // Yellow - Prohibit Loading
                    Log.Information("[状态] 设置背景为 黄色 (禁止上包) - 因新包裹到达且当前正忙");
                });
            }
        }
    }

    // *** ADDED: Method to process a single package ***
    private async Task ProcessSinglePackageAsync(PackageInfo package, CancellationToken cancellationToken)
    {
        Log.Information("[处理:{Index}] 开始处理包裹: {Barcode}", package.Index, package.Barcode);

        // 1. Set UI to Prohibit Loading state (Yellow)
        Application.Current.Dispatcher.Invoke(() =>
        {
            MainWindowBackgroundBrush = BackgroundTimeout; // Yellow - Prohibit Loading
            Log.Information("[状态] 设置背景为 黄色 (禁止上包) - 处理开始");
            // Update basic info immediately
            CurrentBarcode = package.Barcode;
            UpdatePackageInfoItemsBasic(package); // Update weight, size, time
            // Update status display
            var statusItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "状态");
            if (statusItem == null) return;
            statusItem.Value = $"处理中 (序号: {package.Index})";
            statusItem.Description = "准备获取重量...";
            statusItem.StatusColor = "#FFC107"; // Yellow color for status text
        });


        // --- Initial Checks ---
        if (cancellationToken.IsCancellationRequested)
        {
            Log.Warning("[处理:{Index}] 处理在开始时被取消: {Barcode}", package.Index, package.Barcode);
            package.ReleaseImage();
            FinalizeProcessing(null); // Finalize without starting next
            return;
        }

        if (string.Equals(package.Barcode, "noread", StringComparison.OrdinalIgnoreCase))
        {
            Log.Information("[处理:{Index}] 收到 'noread' 条码，跳过处理", package.Index);
            _ = _audioService.PlayPresetAsync(AudioType.WaitingScan);
            package.ReleaseImage();
            FinalizeProcessing(null); // Finalize and allow next if pending
            return;
        }

        // Check PLC Status (moved here from OnPackageInfo)
        if (DeviceStatusText != "正常")
        {
            Log.Warning("[处理:{Index}] PLC状态异常 ({Status})，无法处理包裹: {Barcode}",
                package.Index, DeviceStatusText, package.Barcode);
            package.SetStatus(PackageStatus.Error, $"PLC状态异常: {DeviceStatusText}");
            Application.Current.Dispatcher.Invoke(() =>
                UpdateUiFromResult(package)); // Update final UI for this package
            _ = _audioService.PlayPresetAsync(AudioType.PlcDisconnected);
            IsPlcAbnormalWarningVisible = !IsPlcRejectWarningVisible; // Show abnormal only if reject isn't shown
            package.ReleaseImage();
            FinalizeProcessing(package); // Finalize and allow next if pending
            return;
        }

        // Reset PLC abnormal warning if it was visible
        if (IsPlcAbnormalWarningVisible) IsPlcAbnormalWarningVisible = false;


        // --- Get Weight ---
        var weightTask = Task.Run(() => _weightService.FindNearestWeight(package.CreateTime), cancellationToken);
        try
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var statusItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "状态");
                if (statusItem != null) statusItem.Description = "获取重量中...";
            });
            Log.Debug("[处理:{Index}] 等待重量查询结果: {Barcode}", package.Index, package.Barcode);
            var weightFromScale = await weightTask;
            Log.Debug("[处理:{Index}] 重量查询完成: {Barcode}", package.Index, package.Barcode);

            // Process weight result
            if (weightFromScale is > 0)
            {
                var weightInKg = weightFromScale.Value / 1000.0;
                package.SetWeight(weightInKg);
                Log.Information("[处理:{Index}] 从重量称获取到重量: {Weight}kg, 包裹 {Barcode}",
                    package.Index, weightInKg, package.Barcode);
            }
            else
            {
                var packageWeightOriginal = package.Weight; // Get original weight before potentially setting minimum
                if (packageWeightOriginal <= 0)
                {
                    var weightSettings = _settingsService.LoadSettings<WeightSettings>();
                    var minimumWeight = weightSettings.MinimumWeight / 1000.0;
                    package.SetWeight(minimumWeight);
                    Log.Warning("[处理:{Index}] 未获取到有效重量，使用最小重量: {MinWeight}kg, 包裹 {Barcode}",
                        package.Index, minimumWeight, package.Barcode);
                }
                else
                {
                    package.SetWeight(packageWeightOriginal); // Ensure original is set back if > 0
                    Log.Information("[处理:{Index}] 重量称未返回有效重量，保留原始重量: {Weight}kg, 包裹 {Barcode}",
                        package.Index, packageWeightOriginal, package.Barcode);
                }
            }

            // Update UI with final weight
            Application.Current.Dispatcher.Invoke(() => UpdatePackageInfoItemsBasic(package));
        }
        catch (OperationCanceledException)
        {
            Log.Warning("[处理:{Index}] 重量查询被取消: {Barcode}", package.Index, package.Barcode);
            package.SetStatus(PackageStatus.Error, "操作取消 (重量查询)");
            Application.Current.Dispatcher.Invoke(() => UpdateUiFromResult(package));
            package.ReleaseImage();
            FinalizeProcessing(package);
            return;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[处理:{Index}] 获取重量时出错: {Barcode}", package.Index, package.Barcode);
            package.SetStatus(PackageStatus.Error, $"获取重量失败: {ex.Message}");
            Application.Current.Dispatcher.Invoke(() => UpdateUiFromResult(package));
            _ = _audioService.PlayPresetAsync(AudioType.SystemError);
            package.ReleaseImage();
            FinalizeProcessing(package); // Finalize and allow next if pending
            return;
        }


        // --- Send Upload Request ---
        try
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var statusItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "状态");
                if (statusItem != null)
                {
                    statusItem.Value = $"发送请求 (序号: {package.Index})";
                    statusItem.Description = "正在请求PLC上包...";
                }

                _ = _audioService.PlayPresetAsync(AudioType.WaitingForLoading); // Play sound before sending
            });

            Log.Information("[处理:{Index}] 向PLC发送上传请求: Barcode={Barcode}, Weight={Weight}, L={L}, W={W}, H={H}",
                package.Index, package.Barcode, package.Weight, package.Length, package.Width, package.Height);

            var plcRequestTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // *** Step 1: Send request and wait for ACK ***
            (bool IsAccepted, ushort CommandId) ackResult;
            try
            {
                // Update UI to indicate waiting for PLC ACK
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var statusItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "状态");
                    if (statusItem == null) return;
                    statusItem.Value = $"等待PLC确认 (序号: {package.Index})";
                    statusItem.Description = "等待PLC确认接受上包请求...";
                    statusItem.StatusColor = "#FFC107"; // Yellow
                });

                ackResult = await _plcCommunicationService.SendUploadRequestAsync(
                    (float)package.Weight,
                    (float)(package.Length ?? 0),
                    (float)(package.Width ?? 0),
                    (float)(package.Height ?? 0),
                    package.Barcode,
                    string.Empty, // barcode2D - not used currently
                    (ulong)plcRequestTimestamp,
                    cancellationToken); // Pass cancellation token
            }
            catch (OperationCanceledException) // Cancellation during ACK wait
            {
                Log.Warning("[处理:{Index}] 等待PLC ACK时操作被取消: {Barcode}", package.Index, package.Barcode);
                package.SetStatus(PackageStatus.Error, "操作取消 (等待PLC确认)");
                Application.Current.Dispatcher.Invoke(() =>
                    MainWindowBackgroundBrush = BackgroundSuccess); // Allow next scan on cancellation
                throw; // Re-throw to be caught by the outer finally block for cleanup
            }
            catch (Exception ackEx)
            {
                Log.Error(ackEx, "[处理:{Index}] 发送PLC请求或等待ACK时出错: {Barcode}", package.Index, package.Barcode);
                package.SetStatus(PackageStatus.Error, $"PLC通信错误 (ACK): {ackEx.Message}");
                Application.Current.Dispatcher.Invoke(() =>
                    MainWindowBackgroundBrush = BackgroundSuccess); // Allow next scan on error
                throw; // Re-throw to be caught by the outer finally block for cleanup
            }

            // --- Process ACK Result ---
            if (!ackResult.IsAccepted)
            {
                Log.Warning("[处理:{Index}] 包裹上包请求被PLC拒绝: {Barcode}, CommandId={CommandId}",
                    package.Index, package.Barcode, ackResult.CommandId);
                package.SetStatus(PackageStatus.Error, $"上包拒绝 (序号: {package.Index})");
                _ = _audioService.PlayPresetAsync(AudioType.LoadingRejected);

                // *** Set background to Red on rejection ***
                Application.Current.Dispatcher.Invoke(() =>
                {
                    MainWindowBackgroundBrush = BackgroundRejected; // 设置红色背景
                    Log.Information("[状态] 设置背景为 红色 (拒绝上包)");
                    ShowPlcRejectionWarning(); // 显示PLC拒绝警告
                });

                // Background remains Red
                // Finalize UI and processing in the finally block
                return; // Exit processing for this package
            }

            // --- PLC Accepted Request --- 
            Log.Information("[处理:{Index}] PLC接受上包请求: {Barcode}, CommandId={CommandId}",
                package.Index, package.Barcode, ackResult.CommandId);

            // *** Set background to Green: Allow next scan ***
            Application.Current.Dispatcher.Invoke(() =>
            {
                MainWindowBackgroundBrush = BackgroundSuccess;
                Log.Information("[状态] 设置背景为 绿色 (允许上包) - PLC已接受请求");

                // Update UI to indicate waiting for final result
                var statusItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "状态");
                if (statusItem == null) return;
                statusItem.Value = $"等待处理结果 (序号: {package.Index})";
                statusItem.Description = "PLC正在处理，等待最终结果...";
                statusItem.StatusColor = "#4CAF50"; // Green status text now
            });

            // --- Step 2: Wait for Final Result --- 
            (bool WasSuccess, bool IsTimeout, int PackageId) finalResult;
            try
            {
                finalResult =
                    await _plcCommunicationService.WaitForUploadResultAsync(ackResult.CommandId, cancellationToken);
            }
            catch (OperationCanceledException) // Cancellation during final result wait
            {
                Log.Warning("[处理:{Index}] 等待PLC最终结果时操作被取消: {Barcode}", package.Index, package.Barcode);
                package.SetStatus(PackageStatus.Error, "操作取消 (等待PLC结果)");
                // Background remains Green
                throw; // Re-throw to be caught by the outer finally block for cleanup
            }
            catch (Exception finalEx)
            {
                Log.Error(finalEx, "[处理:{Index}] 等待PLC最终结果时出错: {Barcode}", package.Index, package.Barcode);
                package.SetStatus(PackageStatus.Error, $"PLC通信错误 (结果): {finalEx.Message}");
                // Background remains Green
                throw; // Re-throw to be caught by the outer finally block for cleanup
            }

            // --- Process Final Result --- 
            if (!finalResult.WasSuccess) // Timeout or PLC error during processing
            {
                if (finalResult.IsTimeout)
                {
                    Log.Warning("[处理:{Index}] 等待PLC最终结果超时: {Barcode}, CommandId={CommandId}",
                        package.Index, package.Barcode, ackResult.CommandId);
                    package.SetStatus(PackageStatus.Error, $"上包结果超时 (序号: {package.Index})");
                    _ = _audioService.PlayPresetAsync(AudioType.LoadingTimeout);
                }
                else // Other PLC processing error signaled by WasSuccess=false, IsTimeout=false
                {
                    Log.Error("[处理:{Index}] PLC报告上包处理失败 (非超时): {Barcode}, CommandId={CommandId}",
                        package.Index, package.Barcode, ackResult.CommandId);
                    package.SetStatus(PackageStatus.Error, $"PLC处理失败 (序号: {package.Index})");
                    _ = _audioService.PlayPresetAsync(AudioType.SystemError); // Or a more specific sound
                }

                // Background remains Green
                // Finalize UI and processing in the finally block
                return; // Exit processing for this package
            }

            // --- Final Result is Success from PLC ---
            package.SetStatus(PackageStatus.Success, $"上包完成 (PLC流水号: {finalResult.PackageId})");
            Log.Information("[处理:{Index}] 包裹上包成功: {Barcode}, CommandId={CommandId}, 包裹流水号={PackageId}",
                package.Index, package.Barcode, ackResult.CommandId, finalResult.PackageId);
            _ = _audioService.PlayPresetAsync(AudioType.LoadingSuccess);
            // Reset rejection warning if shown (though it shouldn't be if we got success)
            Application.Current.Dispatcher.Invoke(HidePlcRejectionWarning);

            // --- Image Saving and WCS Upload (Only on Success) ---
            if (package.Image != null)
            {
                Log.Debug("[处理:{Index}] 准备保存和上传图像: {Barcode}", package.Index, package.Barcode);
                BitmapSource? imageToSave;
                try
                {
                    // Clone and freeze the image for safety
                    imageToSave = package.Image.Clone();
                    if (imageToSave.CanFreeze) imageToSave.Freeze();
                    else Log.Warning("[处理:{Index}] 克隆的图像无法冻结，仍尝试使用: {Barcode}", package.Index, package.Barcode);
                }
                catch (Exception cloneEx)
                {
                    Log.Error(cloneEx, "[处理:{Index}] 克隆或冻结图像时出错: {Barcode}", package.Index, package.Barcode);
                    imageToSave = null;
                }

                if (imageToSave != null)
                {
                    var imagePath =
                        await _imageStorageService.SaveImageAsync(imageToSave, package.Barcode, package.CreateTime);
                    if (imagePath != null)
                    {
                        package.ImagePath = imagePath;
                        Log.Information("[处理:{Index}] 图像保存成功: Path={ImagePath}", package.Index, imagePath);

                        if (_jdWcsCommunicationService.IsConnected)
                        {
                            Log.Information("[处理:{Index}] 开始上传图片地址到京东WCS: TaskNo={TaskNo}", package.Index,
                                finalResult.PackageId); // Use PackageId from final result
                            var uploadSuccess = await _jdWcsCommunicationService.UploadImageUrlsAsync(
                                finalResult.PackageId, // Use PackageId from final result
                                [package.Barcode], [], [imagePath],
                                (long)(package.CreateTime.ToUniversalTime() - DateTimeOffset.UnixEpoch)
                                .TotalMilliseconds, cancellationToken);
                            if (uploadSuccess)
                                Log.Information("[处理:{Index}] 图片地址上传成功: TaskNo={TaskNo}, Path={Path}",
                                    package.Index, finalResult.PackageId, imagePath); // Use PackageId from final result
                            else
                            {
                                Log.Warning("[处理:{Index}] 图片地址上传失败: TaskNo={TaskNo}, Path={Path}", package.Index,
                                    finalResult.PackageId, imagePath); // Use PackageId from final result
                                // 使用 SetStatus 更新显示信息
                                var currentStatus = package.Status;
                                var currentDisplay = package.StatusDisplay;
                                package.SetStatus(currentStatus, $"{currentDisplay} [WCS上传失败]");
                            }
                        }
                        else
                        {
                            Log.Warning("[处理:{Index}] 京东WCS未连接，无法上传图片地址: Path={Path}", package.Index, imagePath);
                            // 使用 SetStatus 更新显示信息
                            var currentStatus = package.Status;
                            var currentDisplay = package.StatusDisplay;
                            package.SetStatus(currentStatus, $"{currentDisplay} [WCS未连接]");
                        }
                    }
                    else
                    {
                        Log.Error("[处理:{Index}] 包裹图像保存失败: {Barcode}", package.Index, package.Barcode);
                        // 使用 SetStatus 更新显示信息
                        var currentStatus = package.Status;
                        var currentDisplay = package.StatusDisplay;
                        package.SetStatus(currentStatus, $"{currentDisplay} [图像保存失败]");
                    }
                }
            }
            else
            {
                Log.Warning("[处理:{Index}] 包裹信息中无图像可保存: {Barcode}", package.Index, package.Barcode);
                // 使用 SetStatus 更新显示信息
                var currentStatus = package.Status;
                var currentDisplay = package.StatusDisplay;
                package.SetStatus(currentStatus, $"{currentDisplay} [无图像]");
            }
        } // End of outer try block (after weight check)
        catch (OperationCanceledException) // Catch cancellations from ACK or Final Result awaits
        {
            Log.Warning("[处理:{Index}] 处理PLC请求时操作被取消: {Barcode}", package.Index, package.Barcode);
            // Status should already be set by the inner catch block
            // Background should remain Green (ready for next scan)
        }
        catch (Exception ex) // Catch general exceptions from ACK or Final Result awaits
        {
            Log.Error(ex, "[处理:{Index}] 处理PLC请求时发生未预料错误: {Barcode}", package.Index, package.Barcode);
            // Status should already be set by the inner catch block, or set a generic one if not
            if (package.Status == PackageStatus.Created) // Check if status wasn't set by inner catch
            {
                package.SetStatus(PackageStatus.Error, $"未知PLC通信错误: {ex.Message}");
            }

            _ = _audioService.PlayPresetAsync(AudioType.SystemError);
            // Background should remain Green (ready for next scan)
        }
        finally
        {
            // --- Final UI Update for this package ---
            Application.Current.Dispatcher.Invoke(() => UpdateUiFromResult(package));

            // --- Release image resource ---
            package.ReleaseImage();
            Log.Debug("[处理:{Index}] 释放图像资源: {Barcode}", package.Index, package.Barcode);

            // --- Finalize and potentially start next package ---
            FinalizeProcessing(package);
        }
    }


    // *** ADDED: Method to handle Plc Rejection Warning UI ***
    private void ShowPlcRejectionWarning()
    {
        if (IsPlcRejectWarningVisible) return; // Already visible

        // Cancel previous timeout task if any
        _rejectionWarningCts?.Cancel();
        _rejectionWarningCts?.Dispose();
        _rejectionWarningCts = new CancellationTokenSource();

        IsPlcRejectWarningVisible = true;
        IsPlcAbnormalWarningVisible = false; // Ensure other warning is hidden

        // Start timeout task
        _ = StartRejectionWarningTimeoutAsync(_rejectionWarningCts.Token);
        Log.Debug("显示PLC拒绝警告，启动自动隐藏计时器");
    }

    private void HidePlcRejectionWarning()
    {
        if (!IsPlcRejectWarningVisible) return; // Already hidden

        _rejectionWarningCts?.Cancel(); // Cancel timeout task
        IsPlcRejectWarningVisible = false;
        Log.Debug("隐藏PLC拒绝警告");
    }


    // *** ADDED: Method to finalize processing and start the next package if pending ***
    private void FinalizeProcessing(PackageInfo? processedPackage)
    {
        PackageInfo? nextPackage = null;
        lock (_processingLock)
        {
            Log.Debug("[调度] 完成处理包裹: {Barcode}(序号:{Index})",
                processedPackage?.Barcode ?? "N/A", processedPackage?.Index ?? -1);

            _currentlyProcessingPackage = null; // Mark current as finished

            if (_pendingPackage != null)
            {
                // A package is waiting, start processing it
                nextPackage = _pendingPackage;
                _pendingPackage = null;
                _currentlyProcessingPackage = nextPackage; // Mark next as current
                IsNextPackageWaiting = false; // Pending is now current
                Log.Information("[调度] 获取到待处理包裹，准备开始处理: {Barcode}(序号:{Index})", nextPackage.Barcode, nextPackage.Index);
            }
            else
            {
                // No pending package, system is idle
                IsNextPackageWaiting = false;
                Log.Information("[调度] 无待处理包裹，系统空闲");
                // Set background to Green (Allow Loading / Idle)
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (MainWindowBackgroundBrush == BackgroundSuccess) return; // Avoid unnecessary updates
                    MainWindowBackgroundBrush = BackgroundSuccess;
                    Log.Information("[状态] 设置背景为 绿色 (允许上包) - 处理完成且无待处理");
                });
            }
        }

        // If there's a next package, start its processing outside the lock
        if (nextPackage != null)
        {
            _ = ProcessSinglePackageAsync(nextPackage, _viewModelCts.Token);
        }
    }


    /// <summary>
    /// 合并UI更新的辅助方法 - Updates final status, history, stats
    /// </summary>
    private void UpdateUiFromResult(PackageInfo package)
    {
        // This method should run on the UI thread
        // MainWindowBackgroundBrush is now controlled by the processing flow, not result

        // Update last package status flag
        LastPackageWasSuccessful = string.IsNullOrEmpty(package.ErrorMessage);

        // Update Package Info display (Status, Description, Color)
        UpdatePackageInfoItemsStatusFinal(package); // Update final status text/color

        // Update History and Statistics
        UpdatePackageHistory(package);
        UpdateStatistics(package);

        // Optionally, add a brief visual flash based on result? (e.g., flash red on error)
        // For now, we stick to the Yellow/Green loading state background.
    }

    /// <summary>
    /// 更新包裹信息项的最终状态部分 (Status Text, Color, Description)
    /// </summary>
    private void UpdatePackageInfoItemsStatusFinal(PackageInfo package)
    {
        var statusItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "状态");
        if (statusItem == null) return;

        if (string.IsNullOrEmpty(package.ErrorMessage)) // Success
        {
            statusItem.Value = package.StatusDisplay; // Use provided display or default
            statusItem.Description = $"PLC 包裹流水号: {GetPackageIdFromInformation(package.StatusDisplay)}";
            statusItem.StatusColor = "#4CAF50"; // Green
            // MainWindowBackgroundBrush = BackgroundSuccess; // Moved to flow control
            // HidePlcRejectionWarning(); // Moved to flow control (on success)
        }
        else // Error
        {
            statusItem.Value = package.ErrorMessage; // e.g., "上包超时 (序号: X)"
            statusItem.StatusColor = "#F44336"; // Red for general error

            if (package.ErrorMessage.StartsWith("上包超时"))
            {
                statusItem.Description = "上包请求未收到 PLC 响应";
                // MainWindowBackgroundBrush = BackgroundTimeout; // Moved to flow control
                // HidePlcRejectionWarning(); // Moved to flow control (on timeout)
            }
            else if (package.ErrorMessage.StartsWith("上包拒绝"))
            {
                statusItem.Description = "PLC 拒绝了上包请求";
                // MainWindowBackgroundBrush = BackgroundRejected; // Moved to flow control
                // ShowPlcRejectionWarning(); // Handled in ProcessSinglePackageAsync
            }
            else if (package.ErrorMessage.StartsWith("PLC状态异常"))
            {
                statusItem.Description = "PLC设备当前状态不允许上包";
            }
            else // Generic error
            {
                statusItem.Description = $"处理失败 (序号: {package.Index})";
                // MainWindowBackgroundBrush = BackgroundError; // Moved to flow control
                // HidePlcRejectionWarning(); // Moved to flow control (on other errors)
            }
        }
    }

    /// <summary>
    /// 更新非状态项的新辅助方法，可在异步操作之前调用
    /// </summary>
    private void UpdatePackageInfoItemsBasic(PackageInfo package)
    {
        // Ensure runs on UI thread
        Application.Current.Dispatcher.Invoke(() =>
        {
            var weightItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "重量");
            if (weightItem != null)
            {
                weightItem.Value =
                    package.Weight.ToString("F2", CultureInfo.InvariantCulture); // Format to 2 decimal places
                weightItem.Unit = "kg";
            }

            var sizeItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "尺寸");
            if (sizeItem != null)
            {
                sizeItem.Value = package.VolumeDisplay;
                sizeItem.Unit = "mm"; // 确保单位正确设置
            }

            var timeItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "时间");
            if (timeItem == null) return;
            timeItem.Value = package.CreateTime.ToString("HH:mm:ss");
            timeItem.Description = $"处理于 {package.CreateTime:yyyy-MM-dd}";
        });
    }


    /// <summary>
    /// 更新包裹历史记录
    /// </summary>
    private void UpdatePackageHistory(PackageInfo package)
    {
        // Ensure runs on UI thread
        Application.Current.Dispatcher.Invoke(() =>
        {
            try
            {
                // 限制历史记录数量，保持最新的1000条记录
                const int maxHistoryCount = 1000;

                // Ensure StatusDisplay is set reasonably if null/empty
                if (string.IsNullOrEmpty(package.StatusDisplay))
                {
                    var defaultDisplay = string.IsNullOrEmpty(package.ErrorMessage)
                        ? $"成功 (序号: {package.Index})"
                        : $"{package.ErrorMessage} (序号: {package.Index})";
                    // 使用 SetStatus 设置默认显示信息
                    package.SetStatus(package.Status, defaultDisplay);
                }


                // Create a shallow copy for the history to avoid UI holding onto the main object?
                // Or assume PackageInfo is designed to be displayed as is. Let's assume the latter for now.
                PackageHistory.Insert(0, package);

                // 如果超出最大数量，移除多余的记录
                while (PackageHistory.Count > maxHistoryCount)
                {
                    // Optionally dispose the removed item IF PackageInfo implemented IDisposable correctly
                    // (Current PackageInfo Dispose doesn't seem essential for history items if image is already released)
                    PackageHistory.RemoveAt(PackageHistory.Count - 1);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "更新历史包裹列表时发生错误");
            }
        });
    }

    /// <summary>
    /// 从状态显示文本中提取PackageId
    /// </summary>
    [GeneratedRegex(@"包裹流水号:\s*(\d+)")]
    private static partial Regex PackageIdRegex();

    /// <summary>
    /// 提取PackageId的辅助方法
    /// </summary>
    private static string GetPackageIdFromInformation(string? statusDisplay)
    {
        if (string.IsNullOrEmpty(statusDisplay)) return "N/A";
        // 使用生成的正则表达式
        var match = PackageIdRegex().Match(statusDisplay);
        return match.Success ? match.Groups[1].Value : "N/A";
    }

    private void UpdateStatistics(PackageInfo package)
    {
        // Ensure runs on UI thread
        Application.Current.Dispatcher.Invoke(() =>
        {
            try
            {
                // 更新总包裹数
                var totalItem = StatisticsItems.FirstOrDefault(static x => x.Label == "总包裹数");
                if (totalItem != null)
                {
                    totalItem.Value =
                        int.TryParse(totalItem.Value, out var total)
                            ? (total + 1).ToString()
                            : "1"; // Reset if parse fails
                }

                // 更新成功/失败数
                var isSuccess = string.IsNullOrEmpty(package.ErrorMessage);
                var targetLabel = isSuccess ? "成功数" : "失败数";
                var statusItem = StatisticsItems.FirstOrDefault(x => x.Label == targetLabel);
                if (statusItem != null)
                {
                    statusItem.Value =
                        int.TryParse(statusItem.Value, out var count)
                            ? (count + 1).ToString()
                            : "1"; // Reset if parse fails
                }

                // 更新处理速率（每小时包裹数）
                var speedItem = StatisticsItems.FirstOrDefault(static x => x.Label == "处理速率");
                if (speedItem == null || PackageHistory.Count < 2) return;

                // Use the assigned processing times if available, otherwise fallback to CreateTime
                var latestTime = package.CreateTime; // Use actual package time (processedPackage is not in scope)
                // Find the earliest time in the history (consider limiting history size for perf)
                var earliestTime = PackageHistory.Count > 0 ? PackageHistory[^1].CreateTime : latestTime;


                var timeSpan = latestTime - earliestTime;

                if (timeSpan.TotalSeconds > 1) // Avoid division by zero or tiny intervals
                {
                    // 计算每小时处理数量
                    var hourlyRate = PackageHistory.Count / timeSpan.TotalHours;
                    speedItem.Value = Math.Round(hourlyRate).ToString(CultureInfo.InvariantCulture);
                }
                else if (PackageHistory.Count > 0)
                {
                    // If timespan too small, estimate based on count / small time
                    var estimatedRate = PackageHistory.Count / (timeSpan.TotalSeconds / 3600.0); // Estimate hourly
                    speedItem.Value = Math.Round(estimatedRate).ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    speedItem.Value = "0"; // Not enough data
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "更新统计信息时发生错误");
            }
        });
    }

    /// <summary>
    /// 异步任务，用于在超时后隐藏PLC拒绝警告
    /// </summary>
    private async Task StartRejectionWarningTimeoutAsync(CancellationToken token)
    {
        try
        {
            // 从配置加载超时时间
            var timeoutSeconds = _settingsService.LoadSettings<HostConfiguration>().UploadTimeoutSeconds;
            if (timeoutSeconds <= 0)
            {
                timeoutSeconds = 5; // Default to 5 seconds if config is invalid
                Log.Warning("UploadTimeoutSeconds 配置无效 ({Value})，PLC拒绝警告将使用默认 {Default} 秒超时",
                    _settingsService.LoadSettings<HostConfiguration>().UploadTimeoutSeconds, timeoutSeconds);
            }

            Log.Information("PLC拒绝警告将在 {TimeoutSeconds} 秒后自动隐藏", timeoutSeconds);
            await Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), token);

            // If not cancelled, hide the warning
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (token.IsCancellationRequested) return; // Double check after invoke
                HidePlcRejectionWarning(); // Use the helper method
                Log.Information("PLC拒绝警告已超时自动隐藏");
            });
        }
        catch (OperationCanceledException)
        {
            // Expected exception when cancelled externally
            Log.Debug("PLC拒绝警告超时任务被取消");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理PLC拒绝警告超时时出错");
            // Attempt to hide the warning on error too
            Application.Current.Dispatcher.Invoke(HidePlcRejectionWarning);
        }
    }

    /// <summary>
    /// 获取条码的前缀（移除 "-1-1-" 后缀）
    /// </summary>
    private static string GetBarcodePrefix(string? barcode)
    {
        const string suffix = "-1-1-";
        if (barcode != null && barcode.EndsWith(suffix))
        {
            return barcode[..^suffix.Length];
        }

        return barcode ?? string.Empty;
    }

    /// <summary>
    /// 合并两个相关的 PackageInfo 对象
    /// </summary>
    private static PackageInfo MergePackageInfo(PackageInfo p1, PackageInfo p2)
    {
        // 确定哪个是基础包（无后缀），哪个是后缀包
        var basePackage = p1.Barcode.EndsWith("-1-1-") ? p2 : p1;
        var suffixPackage = p1.Barcode.EndsWith("-1-1-") ? p1 : p2;

        // Create new PackageInfo - DO NOT REUSE INDEX from p1/p2 here. Index assigned later.
        var mergedPackage = PackageInfo.Create();

        // Use the earlier CreateTime
        mergedPackage.SetTriggerTimestamp(basePackage.CreateTime < suffixPackage.CreateTime
            ? basePackage.CreateTime
            : suffixPackage.CreateTime);

        // 合并条码: prefix;suffix-barcode
        var prefix = GetBarcodePrefix(basePackage.Barcode);
        // Ensure suffixPackage barcode is not null/empty before combining
        var combinedBarcode =
            string.IsNullOrEmpty(suffixPackage.Barcode) ? prefix : $"{prefix},{suffixPackage.Barcode}";
        mergedPackage.SetBarcode(combinedBarcode);


        // 优先使用 basePackage 的数据，如果缺失则用 suffixPackage 的
        mergedPackage.SetSegmentCode(basePackage.SegmentCode);
        mergedPackage.SetWeight(basePackage.Weight > 0 ? basePackage.Weight : suffixPackage.Weight);

        // 优先使用 basePackage 的尺寸
        if (basePackage is { Length: > 0, Width: > 0, Height: > 0 })
        {
            mergedPackage.SetDimensions(basePackage.Length.Value, basePackage.Width.Value, basePackage.Height.Value);
        }
        else if (suffixPackage is { Length: > 0, Width: > 0, Height: > 0 })
        {
            mergedPackage.SetDimensions(suffixPackage.Length.Value, suffixPackage.Width.Value,
                suffixPackage.Height.Value);
        }

        // 图像处理：优先使用 basePackage 的图像，并克隆/冻结
        BitmapSource? imageToUse = null;
        string? imagePathToUse = null; // 保留图像路径（如果可用）

        var sourceImage = basePackage.Image ?? suffixPackage.Image;
        var sourceImagePath = basePackage.ImagePath ?? suffixPackage.ImagePath; // 获取可能的路径

        if (sourceImage != null)
        {
            try
            {
                imageToUse = sourceImage.Clone(); // 克隆图像
                if (imageToUse.CanFreeze)
                {
                    imageToUse.Freeze(); // 冻结以确保线程安全
                    Log.Debug("为合并后的包裹克隆并冻结了图像: OriginalBarcode={BaseBarcode}", basePackage.Barcode);
                }
                else
                {
                    Log.Warning("为合并后的包裹克隆的图像无法冻结: OriginalBarcode={BaseBarcode}", basePackage.Barcode);
                }

                imagePathToUse = sourceImagePath; // Use associated path
            }
            catch (Exception imgEx)
            {
                Log.Error(imgEx, "为合并后的包裹克隆或冻结图像时出错: OriginalBarcode={BaseBarcode}", basePackage.Barcode);
                imageToUse = null; // Don't set image on error
                imagePathToUse = null;
            }
        }

        mergedPackage.SetImage(imageToUse, imagePathToUse); // 设置克隆/冻结的图像和路径

        // Initial status is Created
        Log.Debug(
            "合并后的包裹信息: Barcode='{Barcode}', Weight={Weight}, Dimensions='{Dims}', ImageSet={HasImage}, ImagePath='{Path}'",
            mergedPackage.Barcode, mergedPackage.Weight, mergedPackage.VolumeDisplay, mergedPackage.Image != null,
            mergedPackage.ImagePath ?? "N/A");


        return mergedPackage;
    }

    /// <summary>
    /// 从配置中加载条码模式设置
    /// </summary>
    private void LoadBarcodeModeFromSettings()
    {
        try
        {
            var config = _settingsService.LoadSettings<HostConfiguration>();
            // 设置模式，但不触发保存 (避免循环)
            _barcodeMode = config.BarcodeMode;
            Log.Information("从配置加载条码模式: {Mode}", GetBarcodeModeDisplayText(_barcodeMode));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载条码模式配置时出错，使用默认模式");
            _barcodeMode = BarcodeMode.MultiBarcode;
        }
    }

    /// <summary>
    /// 保存条码模式到配置
    /// </summary>
    private void SaveBarcodeModeToSettings(BarcodeMode mode)
    {
        try
        {
            var config = _settingsService.LoadSettings<HostConfiguration>();
            if (config.BarcodeMode == mode) return; // No change needed

            config.BarcodeMode = mode;
            _settingsService.SaveSettings(config);
            Log.Information("条码模式已保存到配置: {Mode}", GetBarcodeModeDisplayText(mode));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存条码模式配置时出错");
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
            try
            {
                Log.Information("正在处置 MainWindowViewModel...");
                // Cancel main view model token source
                _viewModelCts.Cancel();

                // 取消订阅事件
                _cameraService.ConnectionChanged -= OnCameraConnectionChanged;
                _plcCommunicationService.DeviceStatusChanged -= OnPlcDeviceStatusChanged;
                _jdWcsCommunicationService.ConnectionChanged -= OnJdWcsConnectionChanged;

                // 释放订阅
                foreach (var subscription in _subscriptions) subscription.Dispose();
                _subscriptions.Clear();

                // 停止定时器
                _timer.Stop();

                // 取消并释放拒绝警告的CTS
                _rejectionWarningCts?.Cancel();
                _rejectionWarningCts?.Dispose();

                // REMOVED: Stop package processing loop (no longer exists)
                // if (_processingLoopCts is { IsCancellationRequested: false }) { ... }

                // REMOVED: Dispose semaphore (no longer exists)
                // _plcSemaphore.Dispose();

                // Dispose main CancellationTokenSource
                _viewModelCts.Dispose();

                // Release any pending package
                _pendingPackage?.ReleaseImage();
                _pendingPackage = null;
                // Release currently processing package if exists (though should be handled by cancellation)
                _currentlyProcessingPackage?.ReleaseImage();
                _currentlyProcessingPackage = null;

                Log.Information("MainWindowViewModel 处置完毕");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "释放资源时发生错误");
            }

        _disposed = true;
    }

    [GeneratedRegex(
        @"^(JD[0-9A-GI-MO-RT-Z]{12}\d)([-,N])([1-9][0-9]{0,5})([-,S])([0-9A-GI-MO-RT-Z]{1,6})([-,H]\w{0,8})?|^(\w{1,5}\d{1,20})([-,N])([1-9][0-9]{0,5})([-,S])([0-9A-GI-MO-RT-Z]{1,6})([-,H]\w{0,8})?|^([Zz][Yy])[A-Za-z0-9]{13}[-][1-9][0-9]*[-][1-9][0-9]*[-]?$|^([A-Z0-9]{8,})(-|N)([1-9]d{0,2})(-|S)([1-9]d{0,2})([-|H][A-Za-z0-9]*)$|^AK.*$|^BX.*$|^BC.*$|^AD.*$",
        RegexOptions.Compiled)]
    private static partial Regex MyRegex();

    // *** 新增: 清理过期的超时前缀记录 ***
    private void CleanupTimedOutPrefixes(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var keysToRemove = _timedOutPrefixes.Where(kvp => kvp.Value < cutoff).Select(kvp => kvp.Key).ToList();
        foreach (var key in keysToRemove.Where(key => _timedOutPrefixes.TryRemove(key, out _)))
        {
            Log.Debug("[State] 清理了过期的超时前缀: {Prefix}", key);
        }
    }

    // 处理PLC设备状态变更事件 - 统一处理所有状态变更
    private void OnPlcDeviceStatusChanged(object? sender, DeviceStatusCode statusCode)
    {
        // 获取对应的状态信息
        var statusText = GetDeviceStatusDisplayText(statusCode);
        var description = GetDeviceStatusDescription(statusCode);
        var color = GetDeviceStatusColor(statusCode);

        // 通过统一方法更新状态 (确保在UI线程)
        UpdateDeviceStatus(
            statusText,
            description,
            color
        );

        // Update the PLC entry in the DeviceStatuses list
        Application.Current.Dispatcher.Invoke(() =>
        {
            var plcStatusEntry = DeviceStatuses.FirstOrDefault(s => s.Name == "PLC");
            if (plcStatusEntry != null)
            {
                plcStatusEntry.Status = statusText; // Use the translated text
                plcStatusEntry.StatusColor = color;
            }
        });


        // 如果PLC状态恢复正常，隐藏PLC异常警告
        if (statusCode != DeviceStatusCode.Normal || !IsPlcAbnormalWarningVisible) return;
        Log.Information("PLC状态恢复正常，隐藏PLC异常警告。");
        Application.Current.Dispatcher.Invoke(() => IsPlcAbnormalWarningVisible = false);
        // PLC异常状态的警告是在 ProcessSinglePackageAsync 中根据当前状态显示的
    }

    // 统一更新设备状态的方法
    private void UpdateDeviceStatus(string statusText, string description, string color)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            try
            {
                if (DeviceStatusText == statusText && DeviceStatusDescription == description &&
                    DeviceStatusColor == color)
                {
                    return; // No change
                }

                // 更新UI属性
                DeviceStatusText = statusText;
                DeviceStatusDescription = description;
                DeviceStatusColor = color;

                Log.Information("设备状态已更新: {Status}, {Description}", statusText, description);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "更新设备状态时发生错误");
            }
        });
    }

    private static string GetDeviceStatusDisplayText(DeviceStatusCode statusCode)
    {
        return statusCode switch
        {
            DeviceStatusCode.Normal => "正常",
            DeviceStatusCode.Disabled => "上位机禁用",
            DeviceStatusCode.GrayscaleError => "灰度仪异常",
            DeviceStatusCode.MainLineStopped => "主线停机",
            DeviceStatusCode.MainLineFault => "主线故障",
            DeviceStatusCode.Disconnected => "未连接",
            _ => "未知状态"
        };
    }

    private static string GetDeviceStatusDescription(DeviceStatusCode statusCode)
    {
        return statusCode switch
        {
            DeviceStatusCode.Normal => "设备运行正常，可以扫码上包",
            DeviceStatusCode.Disabled => "上位机已禁用，无法上包",
            DeviceStatusCode.GrayscaleError => "灰度仪设备异常，请联系维修",
            DeviceStatusCode.MainLineStopped => "主线已停机，请等待恢复",
            DeviceStatusCode.MainLineFault => "主线出现故障，请联系维修",
            DeviceStatusCode.Disconnected => "PLC设备未连接，请检查网络连接",
            _ => "未知状态，请联系管理员"
        };
    }

    private static string GetDeviceStatusColor(DeviceStatusCode statusCode)
    {
        return statusCode switch
        {
            DeviceStatusCode.Normal => "#4CAF50", // 绿色
            DeviceStatusCode.Disabled => "#FFC107", // 黄色
            DeviceStatusCode.GrayscaleError => "#F44336", // Red
            DeviceStatusCode.MainLineStopped => "#FFC107", // Yellow
            DeviceStatusCode.MainLineFault => "#F44336", // Red
            DeviceStatusCode.Disconnected => "#F44336", // 红色
            _ => "#F44336" // 红色
        };
    }

    // 布局相关属性
    private int _gridRows;
    private int _gridColumns;

    /// <summary>
    /// 网格的行数
    /// </summary>
    public int GridRows
    {
        get => _gridRows;
        private set => SetProperty(ref _gridRows, value);
    }

    /// <summary>
    /// 网格的列数
    /// </summary>
    public int GridColumns
    {
        get => _gridColumns;
        private set => SetProperty(ref _gridColumns, value);
    }

    /// <summary>
    /// 根据相机数量计算最佳布局
    /// </summary>
    /// <param name="cameraCount">相机数量</param>
    private void CalculateOptimalLayout(int cameraCount)
    {
        // 根据相机数量确定网格行和列
        switch (cameraCount)
        {
            case 0: // Handle case with 0 cameras
                GridRows = 1;
                GridColumns = 1;
                break;
            case 1:
                // 单相机: 1x1 网格
                GridRows = 1;
                GridColumns = 1;
                break;
            case 2:
                // 双相机: 1x2 网格 (水平排列)
                GridRows = 1;
                GridColumns = 2;
                break;
            case 3:
                // 三相机: 2x2 网格 (第一个相机占据第一行全部)
                GridRows = 2;
                GridColumns = 2;
                break;
            case 4:
                // 四相机: 2x2 网格
                GridRows = 2;
                GridColumns = 2;
                break;
            case 5:
            case 6:
                // 5-6个相机: 2x3 网格
                GridRows = 2;
                GridColumns = 3;
                break;
            case 7:
            case 8:
            case 9:
                // 7-9个相机: 3x3 网格
                GridRows = 3;
                GridColumns = 3;
                break;
            default:
                // 更多相机: 创建近似正方形网格
                GridRows = (int)Math.Ceiling(Math.Sqrt(cameraCount));
                GridColumns = (int)Math.Ceiling((double)cameraCount / GridRows);
                break;
        }

        Log.Information("根据相机数量 {Count} 计算布局: {Rows}x{Columns}", cameraCount, GridRows, GridColumns);
    }

    /// <summary>
    /// 获取指定相机在网格中的位置
    /// </summary>
    /// <param name="cameraIndex">相机索引 (从0开始)</param>
    /// <param name="cameraCount">相机总数</param>
    /// <returns>行、列、行跨度、列跨度</returns>
    private (int row, int column, int rowSpan, int columnSpan) GetCameraPosition(int cameraIndex, int cameraCount)
    {
        if (cameraCount <= 0) return (0, 0, 1, 1); // Default for 0 cameras

        switch (cameraCount)
        {
            // 特殊情况处理
            case 1:
                // 单个相机占据整个网格
                return (0, 0, GridRows, GridColumns); // Span across the calculated grid
            case 3 when cameraIndex == 0:
                // 三个相机时，第一个相机占据整行
                return (0, 0, 1, 2);
            case 3 when cameraIndex > 0:
                // 三个相机时，第2-3个相机在第二行
                return (1, cameraIndex - 1, 1, 1);
        }

        // 标准网格布局计算
        var row = cameraIndex / GridColumns;
        var column = cameraIndex % GridColumns;

        // 确保不超出网格范围
        if (row >= GridRows) row = GridRows - 1;
        if (column >= GridColumns) column = GridColumns - 1;

        // 默认每个相机占一个单元格
        return (row, column, 1, 1);
    }
}