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
using Serilog.Context; // 添加 Serilog.Context 命名空间

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
    private Brush _mainWindowBackgroundBrush = BackgroundSuccess; // 初始为绿色（允许上包）
    private bool _isPlcRejectWarningVisible; // PLC拒绝警告可见性标志
    private bool _isPlcAbnormalWarningVisible; // PLC异常警告可见性标志
    private BarcodeMode _barcodeMode = BarcodeMode.MultiBarcode; // 默认为多条码模式
    private int _selectedBarcodeModeIndex; // 新增：用于绑定 ComboBox 的 SelectedIndex

    // 上包倒计时相关
    private DispatcherTimer? _uploadCountdownTimer;
    private int _uploadCountdownValue;
    private bool _isUploadCountdownVisible;

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
            new SolidColorBrush(Color.FromArgb(0xAA, 0x4C, 0xAF, 0x50)); // 绿色 (增加透明度) - 用于允许上包状态

    private static readonly Brush
        BackgroundTimeout =
            new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xC1, 0x07)); // 黄色 (增加透明度) - 用于禁止上包状态

    private PackageInfo? _currentlyProcessingPackage;
    private readonly object _processingLock = new();
    private readonly CancellationTokenSource _viewModelCts = new(); // 视图模型的主要取消标记

    private bool _isNextPackageWaiting; // 用于UI指示 (现在指示 _packageStack 是否非空)

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

        // 初始化上包倒计时器
        InitializeUploadCountdownTimer();

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

        // --- 包裹流处理 (优化日志) ---
        _subscriptions.Add(_cameraService.PackageStream
            .ObserveOn(Scheduler.Default)
            .Do(pkg => Log.Verbose("[Stream] 包裹原始事件: Idx={Index}, Barcode='{Barcode}', TS={Timestamp:O}", pkg.Index,
                pkg.Barcode, pkg.CreateTime)) // 更详细的原始事件日志
            .Where(FilterBarcodeByMode) // 根据当前条码模式过滤包裹
            .Do(pkg => Log.Debug("[Stream][Filter] 包裹通过模式过滤 (Mode: {Mode}): Idx={Index}, Barcode='{Barcode}'",
                BarcodeMode, pkg.Index, pkg.Barcode)) // 记录通过过滤
            .GroupBy(p =>
            {
                var prefix = GetBarcodePrefix(p.Barcode);
                Log.Verbose("[Stream][Group] 创建或加入分组: Prefix='{Prefix}', Idx={Index}, Barcode='{Barcode}'", prefix,
                    p.Index, p.Barcode); // Grouping 日志改为 Verbose
                return prefix;
            })
            .SelectMany(group => group
                    // Buffer 超时或数量满足时触发
                    .Buffer(TimeSpan.FromMilliseconds(500), 2) // 保持 Buffer 设置
                    .Where(buffer => buffer.Count > 0) // 确保 Buffer 不为空
            )
            .ObserveOn(Scheduler.Default) // 在后台线程处理 Buffer 结果
            .Subscribe(async void (buffer) => // 使用 async lambda
                {
                    var firstPackage = buffer[0];
                    var currentPrefix = GetBarcodePrefix(firstPackage.Barcode);
                    var initialPackageContext = $"[临时|{currentPrefix}]"; // 用于配对/超时阶段的临时上下文

                    using (LogContext.PushProperty("PackageContext", initialPackageContext)) // 应用临时上下文
                    {
                        Log.Debug(
                            "[Stream][Buffer] 处理 Buffer (Count: {Count}). Prefix='{Prefix}', First Idx={Index}, First Barcode='{Barcode}'",
                            buffer.Count, currentPrefix, firstPackage.Index, firstPackage.Barcode);

                        // 1. 检查超时缓存
                        if (_timedOutPrefixes.TryGetValue(currentPrefix, out var timeoutTime))
                        {
                            if ((DateTime.UtcNow - timeoutTime) < TimedOutPrefixMaxAge)
                            {
                                if (buffer.Count == 1) // 迟到的包裹
                                {
                                    Log.Warning("[Stream][Discard] 丢弃迟到的单个包裹 (配对已超时): Idx={Index}, Barcode='{Barcode}'",
                                        firstPackage.Index, firstPackage.Barcode);
                                    firstPackage.ReleaseImage();
                                    _timedOutPrefixes.TryRemove(currentPrefix, out _); // 收到后移除标记
                                    return;
                                }

                                // buffer.Count == 2 但之前超时了? (不太可能，但处理)
                                Log.Warning("[Stream] 收到配对，但此前记录了前缀超时，继续处理并移除标记.");
                                _timedOutPrefixes.TryRemove(currentPrefix, out _);
                            }
                            else // 超时记录已过期
                            {
                                _timedOutPrefixes.TryRemove(currentPrefix, out _);
                                Log.Verbose("[State] 清理过期的超时前缀记录: {Prefix}", currentPrefix);
                            }
                        }


                        try
                        {
                            PackageInfo? packageToProcess;
                            switch (buffer.Count)
                            {
                                case 2 when BarcodeMode == BarcodeMode.MultiBarcode:
                                    var p1 = buffer.FirstOrDefault(p => !p.Barcode.EndsWith("-1-1-")) ?? buffer[0];
                                    var p2 = buffer.FirstOrDefault(p => p.Barcode.EndsWith("-1-1-")) ?? buffer[1];
                                    Log.Information("[Stream][Pair] 成功配对: P1='{B1}'(Idx:{I1}), P2='{B2}'(Idx:{I2})",
                                        p1.Barcode, p1.Index, p2.Barcode, p2.Index);
                                    packageToProcess = MergePackageInfo(p1, p2);
                                    p1.ReleaseImage();
                                    p2.ReleaseImage();
                                    Log.Debug("[Stream][Merge] 包裹合并完成: Barcode='{MergedBarcode}'",
                                        packageToProcess.Barcode);
                                    _timedOutPrefixes.TryRemove(currentPrefix, out _); // 合并成功，移除超时标记
                                    break;
                                case 2: // 非 MultiBarcode 模式收到两个
                                    PackageInfo packageToKeep;
                                    PackageInfo packageToDiscard;
                                    var p1IsParent = ParentBarcodeRegex.IsMatch(buffer[0].Barcode);
                                    if (BarcodeMode == BarcodeMode.ParentBarcode)
                                    {
                                        packageToKeep = p1IsParent ? buffer[0] : buffer[1];
                                        packageToDiscard = p1IsParent ? buffer[1] : buffer[0];
                                    }
                                    else
                                    {
                                        // ChildBarcode
                                        packageToKeep = !p1IsParent ? buffer[0] : buffer[1];
                                        packageToDiscard = !p1IsParent ? buffer[1] : buffer[0];
                                    }

                                    Log.Information(
                                        "[Stream][FilterPair] 模式 {Mode}: 保留 '{KeepB}'(Idx:{KeepI}), 丢弃 '{DiscardB}'",
                                        BarcodeMode, packageToKeep.Barcode, packageToKeep.Index,
                                        packageToDiscard.Barcode);
                                    packageToProcess = packageToKeep;
                                    packageToDiscard.ReleaseImage();
                                    break;
                                default: // buffer.Count == 1
                                    packageToProcess = firstPackage;
                                    if (BarcodeMode == BarcodeMode.MultiBarcode) // MultiBarcode 超时
                                    {
                                        Log.Warning("[Stream][Timeout] 配对超时，将单独处理: Idx={Index}, Barcode='{Barcode}'",
                                            packageToProcess.Index, packageToProcess.Barcode);
                                        Log.Debug("[State] 记录配对超时前缀: {Prefix}", currentPrefix);
                                        _timedOutPrefixes[currentPrefix] = DateTime.UtcNow;
                                        CleanupTimedOutPrefixes(TimedOutPrefixMaxAge); // 清理旧记录
                                    }
                                    else
                                    {
                                        // Parent/Child 模式单个包裹
                                        Log.Debug(
                                            "[Stream][Single] 模式 {Mode}: 处理单个包裹: Idx={Index}, Barcode='{Barcode}'",
                                            BarcodeMode, packageToProcess.Index, packageToProcess.Barcode);
                                    }

                                    break;
                            }

                            // 3. 分配序号并调用处理入口
                            {
                                var assignedIndex = Interlocked.Increment(ref _viewModelPackageIndex);
                                packageToProcess.Index = assignedIndex; // *** 在传递前分配序号 ***
                                var finalPackageContext =
                                    $"[包裹{assignedIndex}|{packageToProcess.Barcode}]"; // *** 创建最终上下文 ***

                                // *** 在最终上下文中记录接收 ***
                                using (LogContext.PushProperty("PackageContext", finalPackageContext))
                                {
                                    Log.Information("[接收] 包裹进入处理流程");
                                    // 调用包含重量获取的新方法
                                    await FetchWeightAndHandlePackageAsync(packageToProcess, _viewModelCts.Token);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "[Stream] 处理 Buffer (Prefix='{Prefix}', Count={Count}) 时发生内部错误.",
                                currentPrefix, buffer.Count);
                            // 尝试释放 buffer 中的图像
                            foreach (var pkg in buffer) pkg.ReleaseImage();
                        }
                    } // 结束临时上下文 using
                },
                ex => Log.Error(ex, "[Stream] 包裹流处理中发生未处理的顶层异常"))); // 流的顶层错误处理

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

        // *** 初始化 IsNextPackageWaiting ***
        IsNextPackageWaiting = !_packageStack.IsEmpty;

        // 将初始背景设置为绿色
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
        // 初始更新合并PLC状态文本
        OnPlcDeviceStatusChanged(this,
            _plcCommunicationService.IsConnected ? DeviceStatusCode.Normal : DeviceStatusCode.Disconnected);
        // 初始更新WCS状态文本
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
        // 1. 获取状态信息
        var statusText = GetJdWcsStatusDisplayText(isConnected);
        var description = GetJdWcsStatusDescription(isConnected);
        var color = GetJdWcsStatusColor(isConnected);

        // 2. 更新 WCS 特定的 UI 属性
        UpdateJdWcsStatusDisplay(statusText, description, color);

        // 3. 更新 DeviceStatuses 列表中的条目 (确保在 UI 线程)
        Application.Current.Dispatcher.Invoke(() =>
        {
            var jdWcsStatus = DeviceStatuses.FirstOrDefault(static s => s.Name == "京东WCS");
            if (jdWcsStatus != null)
            {
                jdWcsStatus.Status = statusText; // 使用获取到的文本
                jdWcsStatus.StatusColor = color; // 使用获取到的颜色
            }
        });
    }

    // *** 添加: 处理传入包裹的新入口 ***
    private void HandleIncomingPackage(PackageInfo package)
    {
        // 使用最终上下文记录
        Log.Debug("进入 HandleIncomingPackage (调度逻辑)");
        lock (_processingLock)
        {
            if (_currentlyProcessingPackage == null)
            {
                _currentlyProcessingPackage = package;
                Log.Information("[调度] 系统空闲，开始处理");
                _ = ProcessSinglePackageAsync(_currentlyProcessingPackage, _viewModelCts.Token);
            }
            else
            {
                Log.Warning("[调度] 系统正忙 (处理中: Idx={CurrentIndex}), 将新包裹推入堆栈", _currentlyProcessingPackage.Index);
                _packageStack.Push(package);
                IsNextPackageWaiting = true;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (MainWindowBackgroundBrush != BackgroundTimeout) // 避免重复设置
                    {
                        MainWindowBackgroundBrush = BackgroundTimeout;
                        Log.Information("[状态][UI] 设置背景为 黄色 (禁止上包) - 系统忙");
                    }
                });
            }
        }
    }

    // *** 添加: 处理单个包裹的方法 ***
    private async Task ProcessSinglePackageAsync(PackageInfo package, CancellationToken cancellationToken)
    {
        // 上下文应该已由调用者 (FetchWeightAndHandlePackageAsync -> HandleIncomingPackage) 设置
        Log.Information("开始核心处理流程");

        // 1. 更新UI状态 (禁止上包, 显示基础信息)
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (MainWindowBackgroundBrush != BackgroundTimeout)
            {
                MainWindowBackgroundBrush = BackgroundTimeout;
                Log.Information("[状态][UI] 设置背景为 黄色 (禁止上包) - 处理开始");
            }

            CurrentBarcode = package.Barcode;
            UpdatePackageInfoItemsBasic(package); // 更新重量、尺寸、时间
            var statusItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "状态");
            if (statusItem == null) return;
            statusItem.Value = $"处理中 (序号: {package.Index})";
            statusItem.Description = "检查PLC状态..."; // 更新描述
            statusItem.StatusColor = "#FFC107"; // Yellow
        });

        // --- 初始检查 ---
        if (cancellationToken.IsCancellationRequested)
        {
            Log.Warning("处理在开始时被取消.");
            package.ReleaseImage();
            FinalizeProcessing(null); // 取消时不处理下一个
            return;
        }

        // noread 应该在流处理阶段被过滤掉，这里保险起见
        if (string.Equals(package.Barcode, "noread", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("收到 'noread' 条码 (理论上应已过滤)，跳过.");
            _ = _audioService.PlayPresetAsync(AudioType.WaitingScan);
            package.ReleaseImage();
            FinalizeProcessing(null);
            return;
        }

        // 检查PLC状态
        if (DeviceStatusText != "正常") // 直接检查已更新的状态文本
        {
            Log.Warning("PLC状态异常 ({StatusText})，无法处理.", DeviceStatusText); // 使用已更新的文本
            package.SetStatus(PackageStatus.Error, $"PLC状态异常: {DeviceStatusText}");
            Application.Current.Dispatcher.Invoke(() => UpdateUiFromResult(package));
            _ = _audioService.PlayPresetAsync(AudioType.PlcDisconnected);
            IsPlcAbnormalWarningVisible = !IsPlcRejectWarningVisible;
            package.ReleaseImage();
            FinalizeProcessing(package); // 传递 package 以更新 UI
            return;
        }

        if (IsPlcAbnormalWarningVisible) IsPlcAbnormalWarningVisible = false; // 状态正常，隐藏警告

        // --- 发送上包请求 ---
        try
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var statusItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "状态");
                if (statusItem != null) statusItem.Description = "请求PLC上包...";
                _ = _audioService.PlayPresetAsync(AudioType.WaitingForLoading);
            });

            Log.Information("向PLC发送上传请求: W={Weight:F3}, L={L:F1}, W={W:F1}, H={H:F1}",
                package.Weight, package.Length ?? 0, package.Width ?? 0, package.Height ?? 0); // 使用更精确的格式

            var plcRequestTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // 1. 发送请求并等待ACK
            (bool IsAccepted, ushort CommandId) ackResult;
            try
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var statusItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "状态");
                    if (statusItem != null)
                    {
                        statusItem.Value = $"等待PLC确认 (序号: {package.Index})";
                        statusItem.Description = "等待PLC确认接受...";
                        statusItem.StatusColor = "#FFC107"; // Yellow
                    }
                });
                Log.Debug("等待PLC ACK...");
                ackResult = await _plcCommunicationService.SendUploadRequestAsync(
                    (float)package.Weight, (float)(package.Length ?? 0), (float)(package.Width ?? 0),
                    (float)(package.Height ?? 0),
                    package.Barcode, string.Empty, (ulong)plcRequestTimestamp, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Log.Warning("等待PLC ACK时操作被取消.");
                package.SetStatus(PackageStatus.Error, "操作取消 (等待PLC确认)");
                // 取消时，允许下一个扫描
                Application.Current.Dispatcher.Invoke(() => MainWindowBackgroundBrush = BackgroundSuccess);
                throw; // 抛出以触发 finally 清理
            }
            catch (Exception ackEx)
            {
                Log.Error(ackEx, "发送PLC请求或等待ACK时出错.");
                package.SetStatus(PackageStatus.Error, $"PLC通信错误 (ACK): {ackEx.Message}");
                // 出错时，允许下一个扫描
                Application.Current.Dispatcher.Invoke(() => MainWindowBackgroundBrush = BackgroundSuccess);
                throw; // 抛出以触发 finally 清理
            }

            // --- 处理ACK结果 ---
            if (!ackResult.IsAccepted)
            {
                Log.Warning("PLC拒绝上包请求. CommandId={CommandId}", ackResult.CommandId);
                package.SetStatus(PackageStatus.LoadingRejected, $"上包拒绝 (序号: {package.Index})"); // 使用专用状态
                _ = _audioService.PlayPresetAsync(AudioType.LoadingRejected);
                StopUploadCountdown();
                // PLC 拒绝，允许下一个扫描
                Application.Current.Dispatcher.Invoke(() =>
                {
                    MainWindowBackgroundBrush = BackgroundSuccess;
                    Log.Information("[状态][UI] 设置背景为 绿色 (允许上包) - PLC拒绝");
                    // HidePlcRejectionWarning(); // 根据需要是否显示拒绝提示
                });
                // 不再处理此包裹，将在 finally 中更新 UI
                return;
            }

            // --- PLC接受 ---
            Log.Information("PLC接受上包请求. CommandId={CommandId}", ackResult.CommandId);
            Application.Current.Dispatcher.Invoke(() =>
            {
                MainWindowBackgroundBrush = BackgroundSuccess; // PLC 接受，允许下一个扫描
                Log.Information("[状态][UI] 设置背景为 绿色 (允许上包) - PLC接受");
                StartUploadCountdown(); // 开始倒计时
                var statusItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "状态");
                if (statusItem != null)
                {
                    statusItem.Value = $"等待处理结果 (序号: {package.Index})";
                    statusItem.Description = "等待PLC最终结果...";
                    statusItem.StatusColor = "#4CAF50"; // Green
                }
            });

            // 2. 等待最终结果
            (bool WasSuccess, bool IsTimeout, int PackageId) finalResult;
            try
            {
                Log.Debug("等待PLC最终结果...");
                finalResult =
                    await _plcCommunicationService.WaitForUploadResultAsync(ackResult.CommandId, cancellationToken);
                StopUploadCountdown(); // 收到结果，停止倒计时
            }
            catch (OperationCanceledException)
            {
                Log.Warning("等待PLC最终结果时操作被取消.");
                package.SetStatus(PackageStatus.Error, "操作取消 (等待PLC结果)");
                StopUploadCountdown();
                throw; // 抛出以触发 finally 清理
            }
            catch (Exception finalEx)
            {
                Log.Error(finalEx, "等待PLC最终结果时出错.");
                package.SetStatus(PackageStatus.Error, $"PLC通信错误 (结果): {finalEx.Message}");
                StopUploadCountdown();
                throw; // 抛出以触发 finally 清理
            }

            // --- 处理最终结果 ---
            if (!finalResult.WasSuccess) // PLC 失败或超时
            {
                if (finalResult.IsTimeout)
                {
                    Log.Warning("等待PLC最终结果超时. CommandId={CommandId}", ackResult.CommandId);
                    package.SetStatus(PackageStatus.LoadingTimeout, $"上包结果超时 (序号: {package.Index})"); // 专用状态
                    _ = _audioService.PlayPresetAsync(AudioType.LoadingTimeout);
                    // 超时后允许扫描
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        MainWindowBackgroundBrush = BackgroundSuccess;
                        Log.Information("[状态][UI] 设置背景为 绿色 (允许上包) - 上包超时");
                    });
                }
                else
                {
                    // PLC 报告失败
                    Log.Error("PLC报告上包处理失败 (非超时). CommandId={CommandId}", ackResult.CommandId);
                    package.SetStatus(PackageStatus.Error, $"PLC处理失败 (序号: {package.Index})");
                    _ = _audioService.PlayPresetAsync(AudioType.SystemError);
                    // PLC失败，也允许扫描 (背景保持绿色)
                }

                // 不再处理此包裹，将在 finally 中更新 UI
                return;
            }

            // --- PLC 成功 ---
            package.SetStatus(PackageStatus.LoadingSuccess, $"上包完成 (PLC流水号: {finalResult.PackageId})"); // 专用状态
            Log.Information("PLC报告上包成功. CommandId={CommandId}, PLC流水号={PackageId}", ackResult.CommandId,
                finalResult.PackageId);
            _ = _audioService.PlayPresetAsync(AudioType.LoadingSuccess);
            Application.Current.Dispatcher.Invoke(HidePlcRejectionWarning);

            // --- 图像保存和WCS上传 ---
            await HandleImageSavingAndWcsUpload(package, finalResult.PackageId, cancellationToken);
        } // 结束 try (PLC 通信块)
        catch (OperationCanceledException) // 捕获 await 过程中的取消
        {
            Log.Warning("PLC通信或后续处理被取消.");
            // 状态应该已在内部 catch 设置，或保持取消状态
            StopUploadCountdown();
            // 背景色应由内部 catch 或默认设置（绿色）
        }
        catch (Exception ex) // 捕获 PLC 通信块中的其他异常
        {
            Log.Error(ex, "处理PLC通信时发生未预料错误.");
            if (package.Status == PackageStatus.Created) // 如果状态未被内部设置
            {
                package.SetStatus(PackageStatus.Error, $"未知PLC通信错误: {ex.Message}");
            }

            _ = _audioService.PlayPresetAsync(AudioType.SystemError);
            StopUploadCountdown();
            // 背景色应由内部 catch 或默认设置（绿色）
        }
        finally
        {
            Log.Debug("进入 ProcessSinglePackageAsync 的 finally 块");
            // --- 最终UI更新 ---
            Application.Current.Dispatcher.Invoke(() => UpdateUiFromResult(package));
            // --- 释放图像 ---
            package.ReleaseImage();
            Log.Debug("图像资源已释放");
            // --- 结束处理，可能启动下一个 ---
            FinalizeProcessing(package);
            Log.Information("核心处理流程结束");
        }
    }

    // *** 新增: 处理图像保存和WCS上传的辅助方法 ***
    private async Task HandleImageSavingAndWcsUpload(PackageInfo package, int plcPackageId,
        CancellationToken cancellationToken)
    {
        // 上下文应该已由调用者设置
        if (package.Image == null)
        {
            Log.Warning("包裹信息中无图像可保存或上传.");
            var currentDisplay = package.StatusDisplay;
            package.SetStatus(package.Status, $"{currentDisplay} [无图像]");
            return;
        }

        Log.Debug("开始处理图像保存和WCS上传.");
        BitmapSource? imageToSave;
        try
        {
            imageToSave = package.Image.Clone();
            if (imageToSave.CanFreeze) imageToSave.Freeze();
            else Log.Warning("克隆的图像无法冻结，仍尝试使用.");
        }
        catch (Exception cloneEx)
        {
            Log.Error(cloneEx, "克隆或冻结图像时出错.");
            imageToSave = null; // 出错则不处理
        }

        if (imageToSave == null)
        {
            var currentDisplay = package.StatusDisplay;
            package.SetStatus(package.Status, $"{currentDisplay} [图像克隆失败]");
            return;
        }

        // 保存图像
        string? imagePath = null;
        try
        {
            imagePath = await _imageStorageService.SaveImageAsync(imageToSave, package.Barcode, package.CreateTime);
        }
        catch (Exception saveEx)
        {
            Log.Error(saveEx, "异步保存图像时发生异常.");
        }

        if (imagePath != null)
        {
            package.ImagePath = imagePath;
            Log.Information("图像保存成功: Path={ImagePath}", imagePath);

            // 上传WCS
            if (_jdWcsCommunicationService.IsConnected)
            {
                Log.Information("开始上传图片地址到京东WCS: PLC流水号={PlcPackageId}", plcPackageId);
                bool wcsSuccess;
                try
                {
                    wcsSuccess = await _jdWcsCommunicationService.UploadImageUrlsAsync(
                        plcPackageId, [package.Barcode], [], [imagePath],
                        (long)(package.CreateTime.ToUniversalTime() - DateTimeOffset.UnixEpoch).TotalMilliseconds,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Log.Warning("WCS上传被取消.");
                    wcsSuccess = false; // 标记为失败
                }
                catch (Exception wcsEx)
                {
                    Log.Error(wcsEx, "上传图片地址到WCS时发生异常.");
                    wcsSuccess = false;
                }

                if (wcsSuccess)
                {
                    Log.Information("图片地址上传WCS成功: PLC流水号={PlcPackageId}", plcPackageId);
                }
                else
                {
                    Log.Warning("图片地址上传WCS失败: PLC流水号={PlcPackageId}", plcPackageId);
                    var currentDisplay = package.StatusDisplay;
                    package.SetStatus(package.Status, $"{currentDisplay} [WCS上传失败]");
                }
            }
            else
            {
                Log.Warning("京东WCS未连接，无法上传图片地址.");
                var currentDisplay = package.StatusDisplay;
                package.SetStatus(package.Status, $"{currentDisplay} [WCS未连接]");
            }
        }
        else
        {
            Log.Error("包裹图像保存失败.");
            var currentDisplay = package.StatusDisplay;
            package.SetStatus(package.Status, $"{currentDisplay} [图像保存失败]");
        }
        // imageToSave 会在此方法结束后超出作用域
    }


    private void HidePlcRejectionWarning()
    {
        if (!IsPlcRejectWarningVisible) return; // 已经隐藏

        IsPlcRejectWarningVisible = false;
        Log.Debug("隐藏PLC拒绝警告");
    }


    // *** 添加: 处理PLC拒绝警告UI的方法 ***
    private void FinalizeProcessing(PackageInfo? processedPackage)
    {
        PackageInfo? nextPackageToProcess;
        var processedPackageContext = processedPackage != null
            ? $"[包裹{processedPackage.Index}|{processedPackage.Barcode}]"
            : "[未知包裹]";

        using (LogContext.PushProperty("PackageContext", processedPackageContext)) // 为完成的包裹设置上下文
        {
            Log.Information("[调度] Finalize: 完成处理");

            lock (_processingLock)
            {
                if (_currentlyProcessingPackage != null &&
                    !ReferenceEquals(_currentlyProcessingPackage, processedPackage))
                {
                    // 这表示当前处理的包裹和完成的包裹不匹配，可能是一个严重错误
                    Log.Error("[调度][!!!] Finalize 时发现当前处理的包裹 (Idx:{CurrentIdx}) 与完成的包裹 (Idx:{ProcessedIdx}) 不匹配!",
                        _currentlyProcessingPackage.Index, processedPackage?.Index ?? -1);
                    // 在这种情况下，我们应该相信 processedPackage 已经完成，并尝试处理堆栈中的下一个
                }

                _currentlyProcessingPackage = null; // 标记当前为空闲

                // 检查堆栈
                if (_packageStack.TryPop(out nextPackageToProcess))
                {
                    // 从堆栈获取下一个
                    var nextPackageContext = $"[包裹{nextPackageToProcess.Index}|{nextPackageToProcess.Barcode}]";
                    using (LogContext.PushProperty("PackageContext", nextPackageContext)) // 为下一个包裹设置上下文
                    {
                        Log.Information("[调度] 从堆栈获取下一个包裹处理");
                    }

                    _currentlyProcessingPackage = nextPackageToProcess; // 设置为当前处理
                    IsNextPackageWaiting = !_packageStack.IsEmpty; // 更新等待状态

                    // 清理堆栈中可能剩余的其他包裹（除了刚弹出的那个）
                    // 注意：这里传递 nextPackageToProcess 作为排除项
                    ClearPackageStack("[调度] 处理新包裹前清理剩余堆栈", nextPackageToProcess.Index, nextPackageToProcess);
                }
                else // 堆栈为空
                {
                    _currentlyProcessingPackage = null;
                    IsNextPackageWaiting = false;
                    Log.Information("[调度] 堆栈为空，系统转为空闲状态");
                    // 设置背景为绿色 (允许上包 / 空闲)
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (MainWindowBackgroundBrush != BackgroundSuccess)
                        {
                            MainWindowBackgroundBrush = BackgroundSuccess;
                            Log.Information("[状态][UI] 设置背景为 绿色 (允许上包) - 系统空闲");
                        }
                    });
                }
            } // 结束 lock

            // 在锁外启动下一个处理（如果需要）
            if (nextPackageToProcess == null) return;
            var finalNextPackageContext = $"[包裹{nextPackageToProcess.Index}|{nextPackageToProcess.Barcode}]";
            using (LogContext.PushProperty("PackageContext", finalNextPackageContext))
            {
                Log.Debug("[调度] 准备在UI线程外异步启动下一个包裹处理");
            }

            // 使用 Task.Run 确保它不在锁内或 UI 线程上阻塞启动
            _ = Task.Run(() => ProcessSinglePackageAsync(nextPackageToProcess, _viewModelCts.Token));
        } // 结束 processedPackage 的上下文 using
    }


    /// <summary>
    /// 合并UI更新的辅助方法 - 更新最终状态、历史记录、统计信息
    /// </summary>
    private void UpdateUiFromResult(PackageInfo package)
    {
        // 此方法应该在UI线程上运行
        // MainWindowBackgroundBrush现在由处理流程控制，而不是结果

        // 更新最后一个包裹状态标志
        LastPackageWasSuccessful = string.IsNullOrEmpty(package.ErrorMessage);

        // 更新包裹信息显示 (状态, 描述, 颜色)
        UpdatePackageInfoItemsStatusFinal(package); // 更新最终状态文本/颜色

        // 更新历史记录和统计信息
        UpdatePackageHistory(package);
        UpdateStatistics(package);

        // 可选, 根据结果添加一个短暂的视觉闪烁? (例如, 错误时闪烁红色)
        // 目前, 我们坚持使用黄色/绿色的加载状态背景.
    }

    /// <summary>
    /// 更新包裹信息项的最终状态部分 (状态文本, 颜色, 描述)
    /// </summary>
    private void UpdatePackageInfoItemsStatusFinal(PackageInfo package)
    {
        var statusItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "状态");
        if (statusItem == null) return;

        if (string.IsNullOrEmpty(package.ErrorMessage)) // 成功
        {
            statusItem.Value = package.StatusDisplay; // 使用提供的显示或默认
            statusItem.Description = $"PLC 包裹流水号: {GetPackageIdFromInformation(package.StatusDisplay)}";
            statusItem.StatusColor = "#4CAF50"; // 绿色
        }
        else // Error
        {
            statusItem.Value = package.ErrorMessage; // 例如, "上包超时 (序号: X)"

            if (package.ErrorMessage.StartsWith("上包超时"))
            {
                statusItem.Description = "上包请求未收到 PLC 响应";
                statusItem.StatusColor = "#FFC107"; // 黄色 (超时)
            }
            else if (package.ErrorMessage.StartsWith("上包拒绝"))
            {
                statusItem.Description = "PLC 拒绝了上包请求";
                statusItem.StatusColor = "#F44336"; // 红色 (拒绝)
            }
            else if (package.ErrorMessage.StartsWith("PLC状态异常"))
            {
                statusItem.Description = "PLC设备当前状态不允许上包";
                statusItem.StatusColor = "#F44336"; // 红色 (PLC异常状态)
            }
            else // 通用错误
            {
                statusItem.Description = $"处理失败 (序号: {package.Index})";
                statusItem.StatusColor = "#F44336"; // 红色 (通用错误)
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
                    package.Weight.ToString("F2", CultureInfo.InvariantCulture); // 格式化为2位小数
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

                // 确保StatusDisplay合理设置, 如果为空/空
                if (string.IsNullOrEmpty(package.StatusDisplay))
                {
                    var defaultDisplay = string.IsNullOrEmpty(package.ErrorMessage)
                        ? $"成功 (序号: {package.Index})"
                        : $"{package.ErrorMessage} (序号: {package.Index})";
                    // 使用 SetStatus 设置默认显示信息
                    package.SetStatus(package.Status, defaultDisplay);
                }


                // 创建一个浅拷贝以避免UI保持主对象?
                // 或者假设PackageInfo设计为按原样显示。目前假设后者。
                PackageHistory.Insert(0, package);

                // 如果超出最大数量，移除多余的记录
                while (PackageHistory.Count > maxHistoryCount)
                {
                    // 可选地释放已移除的项目(如果PackageInfo正确实现IDisposable)
                    // (当前PackageInfo Dispose似乎不必要用于历史项目, 如果图像已经释放)
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
        // 确保在UI线程上运行
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
                            : "1"; // 如果解析失败则重置
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
                            : "1"; // 如果解析失败则重置
                }

                // 更新处理速率（每小时包裹数）
                var speedItem = StatisticsItems.FirstOrDefault(static x => x.Label == "处理速率");
                if (speedItem == null || PackageHistory.Count < 2) return;

                // 使用已分配的处理时间, 如果可用, 否则回退到CreateTime
                var latestTime = package.CreateTime; // 使用实际包裹时间(processedPackage不在范围内)
                // 在历史中找到最早的时间(考虑限制历史大小以提高性能)
                var earliestTime = PackageHistory.Count > 0 ? PackageHistory[^1].CreateTime : latestTime;


                var timeSpan = latestTime - earliestTime;

                if (timeSpan.TotalSeconds > 1) // 避免除以零或极小的间隔
                {
                    // 计算每小时处理数量
                    var hourlyRate = PackageHistory.Count / timeSpan.TotalHours;
                    speedItem.Value = Math.Round(hourlyRate).ToString(CultureInfo.InvariantCulture);
                }
                else if (PackageHistory.Count > 0)
                {
                    // 如果时间间隔太小, 基于计数/小时间估计
                    var estimatedRate = PackageHistory.Count / (timeSpan.TotalSeconds / 3600.0); // 估计每小时
                    speedItem.Value = Math.Round(estimatedRate).ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    speedItem.Value = "0"; // 数据不足
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "更新统计信息时发生错误");
            }
        });
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

        // 创建新的 PackageInfo - 不要在这里重用 p1/p2 的 Index。稍后分配索引。
        var mergedPackage = PackageInfo.Create();

        // 使用较早的 CreateTime
        mergedPackage.SetTriggerTimestamp(basePackage.CreateTime < suffixPackage.CreateTime
            ? basePackage.CreateTime
            : suffixPackage.CreateTime);

        // 合并条码: prefix;suffix-barcode
        var prefix = GetBarcodePrefix(basePackage.Barcode);
        // 确保 suffixPackage 条码不为空/空
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

        // 初始状态是 Created
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
            if (config.BarcodeMode == mode) return; // 不需要更改

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
        Log.Information("[Dispose] 开始释放 MainWindowViewModel (disposing={IsDisposing})...", disposing);
        if (disposing)
        {
            try
            {
                _viewModelCts.Cancel(); // 取消所有操作
                Log.Debug("[Dispose] CancellationToken已取消.");

                // 取消订阅
                _cameraService.ConnectionChanged -= OnCameraConnectionChanged;
                _plcCommunicationService.DeviceStatusChanged -= OnPlcDeviceStatusChanged;
                _jdWcsCommunicationService.ConnectionChanged -= OnJdWcsConnectionChanged;
                foreach (var sub in _subscriptions) sub.Dispose();
                _subscriptions.Clear();
                Log.Debug("[Dispose] 所有事件订阅已取消并清理.");

                // 停止定时器
                _timer.Stop();
                _uploadCountdownTimer?.Stop();
                if (_uploadCountdownTimer != null) _uploadCountdownTimer.Tick -= UploadCountdownTimer_Tick;
                Log.Debug("[Dispose] 定时器已停止.");

                // 清理包裹状态
                ClearPackageStack("[Dispose] 清理包裹堆栈", -1);
                lock (_processingLock)
                {
                    _currentlyProcessingPackage?.ReleaseImage();
                    _currentlyProcessingPackage = null;
                }

                Log.Debug("[Dispose] 当前处理和堆栈中的包裹已清理.");

                _viewModelCts.Dispose(); // 处置 CancellationTokenSource
                Log.Information("[Dispose] MainWindowViewModel 处置完毕.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Dispose] 释放资源时发生错误");
            }
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

        // 更新 DeviceStatuses 列表中的 PLC 条目
        Application.Current.Dispatcher.Invoke(() =>
        {
            var plcStatusEntry = DeviceStatuses.FirstOrDefault(s => s.Name == "PLC");
            if (plcStatusEntry == null) return;
            plcStatusEntry.Status = statusText; // 使用翻译的文本
            plcStatusEntry.StatusColor = color;
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
                    return;
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

    // --- 倒计时计时器逻辑 ---
    private void InitializeUploadCountdownTimer()
    {
        _uploadCountdownTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _uploadCountdownTimer.Tick += UploadCountdownTimer_Tick;
    }

    private void UploadCountdownTimer_Tick(object? sender, EventArgs e)
    {
        UploadCountdownValue--;
        if (UploadCountdownValue > 0) return;
        StopUploadCountdown();
        Log.Information("上包倒计时结束");
    }

    private void StartUploadCountdown()
    {
        try
        {
            var config = _settingsService.LoadSettings<HostConfiguration>();
            var countdownSeconds = config.UploadCountdownSeconds;

            if (countdownSeconds <= 0)
            {
                Log.Warning("上包倒计时配置无效 ({Value})，将不启动倒计时", countdownSeconds);
                StopUploadCountdown(); // 确保如果配置无效则停止
                return;
            }

            UploadCountdownValue = countdownSeconds;
            IsUploadCountdownVisible = true;
            _uploadCountdownTimer?.Start();
            Log.Information("启动上包倒计时: {Seconds} 秒", countdownSeconds);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动上包倒计时出错");
            StopUploadCountdown(); // 在错误时停止
        }
    }

    private void StopUploadCountdown()
    {
        _uploadCountdownTimer?.Stop();
        IsUploadCountdownVisible = false;
    }

    // 上包倒计时秒数
    public int UploadCountdownValue
    {
        get => _uploadCountdownValue;
        private set => SetProperty(ref _uploadCountdownValue, value);
    }

    // 上包倒计时是否可见
    public bool IsUploadCountdownVisible
    {
        get => _isUploadCountdownVisible;
        private set => SetProperty(ref _isUploadCountdownVisible, value);
    }

    // 新增：WCS 状态辅助方法
    private static string GetJdWcsStatusDisplayText(bool isConnected) => isConnected ? "已连接" : "已断开";

    private static string GetJdWcsStatusDescription(bool isConnected) =>
        isConnected ? "京东WCS服务连接正常，可以上传图片" : "京东WCS服务未连接，请检查网络连接";

    private static string GetJdWcsStatusColor(bool isConnected) => isConnected ? "#4CAF50" : "#F44336";

    // 新增：更新 WCS 特定状态显示的方法
    private void UpdateJdWcsStatusDisplay(string statusText, string description, string color)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            try
            {
                // 检查是否有实际变化，避免不必要的更新
                if (JdStatusText == statusText && JdStatusDescription == description && JdStatusColor == color) return;

                JdStatusText = statusText;
                JdStatusDescription = description;
                JdStatusColor = color;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[ViewModel] 更新京东WCS状态显示时发生错误");
            }
        });
    }

    // *** 新增: 包裹缓冲栈 ***
    private readonly ConcurrentStack<PackageInfo> _packageStack = new();

    // *** 新增: 清理包裹堆栈并释放资源 ***
    private void ClearPackageStack(string reason, int currentPackageIndex, PackageInfo? excludePackage = null)
    {
        var context = currentPackageIndex >= 0 ? $"[清理Ctx:{currentPackageIndex}]" : "[清理Ctx:N/A]";
        using (LogContext.PushProperty("PackageContext", context))
        {
            Log.Debug("开始清理包裹堆栈. 原因: {Reason}", reason);
            var count = 0;
            while (_packageStack.TryPop(out var packageToDiscard))
            {
                var discardContext = $"[包裹{packageToDiscard.Index}|{packageToDiscard.Barcode}]";
                using (LogContext.PushProperty("PackageContext", discardContext)) // 为被丢弃的包裹设置上下文
                {
                    if (excludePackage != null && ReferenceEquals(packageToDiscard, excludePackage))
                    {
                        Log.Debug("跳过释放排除的包裹 (通常是下一个要处理的)");
                        continue;
                    }

                    Log.Warning("正在丢弃并释放堆栈中的包裹");
                    packageToDiscard.ReleaseImage();
                    count++;
                }
            }

            if (count > 0)
            {
                Log.Information("共清理了 {Count} 个堆栈中的包裹", count);
            }
            else
            {
                Log.Debug("包裹堆栈已为空，无需清理.");
            }
            // 更新UI状态 (移到调用者处或 Finalize 中)
            // IsNextPackageWaiting = !_packageStack.IsEmpty;
        }
    }

    // *** 新增: 获取重量并处理包裹的方法 ***
    private async Task FetchWeightAndHandlePackageAsync(PackageInfo package, CancellationToken cancellationToken)
    {
        // *** 在调用者处已设置最终上下文 ***
        Log.Debug("开始获取重量");
        try
        {
            var weightTask = Task.Run(() => _weightService.FindNearestWeight(DateTime.Now), cancellationToken);
            Log.Debug("等待重量查询结果...");
            var weightFromScale = await weightTask;
            Log.Debug("重量查询完成.");

            if (weightFromScale is > 0)
            {
                var weightInKg = weightFromScale.Value / 1000.0;
                package.SetWeight(weightInKg);
                Log.Information("从重量称获取到重量: {WeightKg:F3}kg", weightInKg);
            }
            else
            {
                var packageWeightOriginal = package.Weight;
                if (packageWeightOriginal <= 0)
                {
                    var weightSettings = _settingsService.LoadSettings<WeightSettings>();
                    var minimumWeight = weightSettings.MinimumWeight / 1000.0;
                    package.SetWeight(minimumWeight);
                    Log.Warning("未获取到有效重量，使用最小重量: {MinWeight:F3}kg", minimumWeight);
                }
                else
                {
                    package.SetWeight(packageWeightOriginal);
                    Log.Debug("重量称未返回有效重量，保留原始重量: {WeightKg:F3}kg", packageWeightOriginal);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log.Warning("重量查询被取消.");
            // 继续处理，可能使用默认/0重量
        }
        catch (Exception ex)
        {
            Log.Error(ex, "获取重量时出错.");
            _ = _audioService.PlayPresetAsync(AudioType.SystemError);
            // 继续处理，可能使用默认/0重量
        }

        // 调用调度入口 (继续使用当前 LogContext)
        HandleIncomingPackage(package);
        Log.Debug("重量获取完成，已提交到调度 HandleIncomingPackage");
    }
}