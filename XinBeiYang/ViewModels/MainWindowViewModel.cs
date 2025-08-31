using System.Collections.Concurrent;
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
using Serilog;
using Serilog.Context;
using SharedUI.Models;
using XinBeiYang.Models;
using XinBeiYang.Models.Communication;
using XinBeiYang.Services;

// 添加 System.Threading.Channels 命名空间用于队列处理
using System.Threading.Channels;

// 添加 Serilog.Context 命名空间

// Added for CancellationTokenSource

namespace XinBeiYang.ViewModels;

#region 条码模式枚举

/// <summary>
///     条码模式枚举
/// </summary>
public enum BarcodeMode
{
    /// <summary>
    ///     多条码模式（合并处理）
    /// </summary>
    MultiBarcode,

    /// <summary>
    ///     仅处理母条码（符合正则表达式的条码）
    /// </summary>
    ParentBarcode,

    /// <summary>
    ///     仅处理子条码（不符合正则表达式的条码）
    /// </summary>
    ChildBarcode
}

#endregion

#region 设备状态信息类

/// <summary>
///     设备状态信息类
/// </summary>
public class DeviceStatusInfo(string name, string icon, string status, string statusColor) : BindableBase
{
    private string _icon = icon;
    private string _name = name;
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
///     相机展示信息类
/// </summary>
public class CameraDisplayInfo : BindableBase
{
    private readonly string _cameraId = string.Empty;
    private readonly string _cameraName = string.Empty;
    private int _column;
    private int _columnSpan = 1;
    private BitmapSource? _currentImage;
    private bool _isOnline;
    private int _row;
    private int _rowSpan = 1;

    /// <summary>
    ///     相机ID
    /// </summary>
    public string CameraId
    {
        get => _cameraId;
        init => SetProperty(ref _cameraId, value);
    }

    /// <summary>
    ///     相机名称
    /// </summary>
    public string CameraName
    {
        get => _cameraName;
        init => SetProperty(ref _cameraName, value);
    }

    /// <summary>
    ///     相机是否在线
    /// </summary>
    public bool IsOnline
    {
        get => _isOnline;
        set => SetProperty(ref _isOnline, value);
    }

    /// <summary>
    ///     当前图像
    /// </summary>
    public BitmapSource? CurrentImage
    {
        get => _currentImage;
        set => SetProperty(ref _currentImage, value);
    }

    /// <summary>
    ///     在网格中的行位置
    /// </summary>
    public int Row
    {
        get => _row;
        set => SetProperty(ref _row, value);
    }

    /// <summary>
    ///     在网格中的列位置
    /// </summary>
    public int Column
    {
        get => _column;
        set => SetProperty(ref _column, value);
    }

    /// <summary>
    ///     占用的行数
    /// </summary>
    public int RowSpan
    {
        get => _rowSpan;
        set => SetProperty(ref _rowSpan, value);
    }

    /// <summary>
    ///     占用的列数
    /// </summary>
    public int ColumnSpan
    {
        get => _columnSpan;
        set => SetProperty(ref _columnSpan, value);
    }
}

#endregion

#region 图像处理任务记录

/// <summary>
///     图像处理任务记录
/// </summary>
internal record ImageProcessingTask(
    PackageInfo Package,
    string PackageContext,
    int PackageId,
    BitmapSource? ClonedImage);

#endregion

/// <summary>
///     主窗口视图模型
/// </summary>
public partial class MainWindowViewModel : BindableBase, IDisposable
{
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

    private readonly IAudioService _audioService;
    private readonly ICameraService _cameraService;
    private readonly IDialogService _dialogService;
    private readonly IImageStorageService _imageStorageService;
    private readonly IJdWcsCommunicationService _jdWcsCommunicationService;


    // *** 新增: 包裹缓冲栈 ***
    private readonly ConcurrentStack<PackageInfo> _packageStack = new();
    private readonly IPlcCommunicationService _plcCommunicationService;
    private readonly object _processingLock = new();
    private readonly ISettingsService _settings_service;
    private readonly List<IDisposable> _subscriptions = [];

    // *** 新增: 用于存储最近超时的条码前缀 ***
    private readonly ConcurrentDictionary<string, DateTime> _timedOutPrefixes = new();
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _viewModelCts = new(); // 视图模型的主要取消标记
    private readonly SerialPortWeightService _weight_service; // 添加重量称服务依赖

    // *** 新增: 图像处理队列 - 用于顺序处理图像保存和上传，避免并发问题 ***
    private readonly Channel<ImageProcessingTask> _imageProcessingChannel;
    private readonly Task _imageProcessingTask;
    private readonly CancellationTokenSource _imageProcessingCts = new();
    private BarcodeMode _barcodeMode = BarcodeMode.MultiBarcode; // 默认为多条码模式

    // 新增：相机初始化标志
    private bool _camerasInitialized;
    private string _currentBarcode = string.Empty;
    private BitmapSource? _currentImage;

    private PackageInfo? _currentlyProcessingPackage;
    private bool _disposed;
    private int _gridColumns;

    // 布局相关属性
    private int _gridRows;

    private bool _isNextPackageWaiting; // 用于UI指示 (现在指示 _packageStack 是否非空)
    private bool _isPlcAbnormalWarningVisible; // PLC异常警告可见性标志
    private bool _isUploadCountdownVisible;

    // 等待PLC上包确认的遮罩层相关属性
    private bool _isWaitingForUploadResult;

    // JD WCS状态独立属性
    private string _jdWcsStatusColor = "#F44336";
    private string _jdWcsStatusDescription = "京东WCS服务未连接，请检查网络连接";
    private string _jdWcsStatusText = "未连接";

    private bool _lastPackageWasSuccessful = true; // 初始状态为成功
    private Brush _mainWindowBackgroundBrush = BackgroundSuccess; // 初始为绿色（允许上包）

    // PLC状态独立属性
    private string _plcStatusColor = "#F44336";
    private string _plcStatusDescription = "PLC设备未连接，请检查网络连接";
    private string _plcStatusText = "未连接";
    private int _selectedBarcodeModeIndex; // 新增：用于绑定 ComboBox 的 SelectedIndex
    private SystemStatus _systemStatus = new();

    // 上包倒计时相关
    private DispatcherTimer? _uploadCountdownTimer;
    private int _uploadCountdownValue;

    private int _viewModelPackageIndex;
    private double _waitingCountdownProgress;
    private int _waitingCountdownSeconds;

    // 等待PLC确认倒计时相关
    private readonly object _countdownLock = new();
    private DispatcherTimer? _waitingCountdownTimer;
    private int _waitingCountdownTotalSeconds;
    private string _waitingStatusText = "等待PLC确认上包...";

    public MainWindowViewModel(
        IDialogService dialogService,
        ICameraService cameraService,
        IAudioService audioService,
        IPlcCommunicationService plcCommunicationService,
        IJdWcsCommunicationService jdWcsCommunicationService,
        IImageStorageService imageStorageService,
        ISettingsService settingsService,
        SerialPortWeightService weightService)
    {
        _dialogService = dialogService;
        _cameraService = cameraService;
        _audioService = audioService;
        _plcCommunicationService = plcCommunicationService;
        _jdWcsCommunicationService = jdWcsCommunicationService;
        _imageStorageService = imageStorageService;
        _settings_service = settingsService;
        _weight_service = weightService; // 直接注入 SerialPortWeightService 实例

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

        // 初始化图像处理队列 - 使用有界的通道来控制并发数量
        _imageProcessingChannel = Channel.CreateBounded<ImageProcessingTask>(
            new BoundedChannelOptions(10) // 最多缓冲10个图像处理任务
            {
                FullMode = BoundedChannelFullMode.Wait, // 队列满时等待
                SingleReader = true, // 单个读取器
                SingleWriter = false // 多个写入器
            });

        // 启动图像处理任务 - 顺序处理队列中的任务
        _imageProcessingTask = Task.Run(ProcessImageQueueAsync, _imageProcessingCts.Token);

        // 初始化设备状态
        InitializeDeviceStatuses();

        // 初始化PLC和JD WCS独立状态属性
        InitializeIndividualDeviceStatuses();

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

        // 订阅JD WCS连接状态变更事件
        _jdWcsCommunicationService.ConnectionChanged += OnJdWcsConnectionChanged;


        // *** 移除: 不再订阅PLC最终结果事件 ***
        // _plcCommunicationService.UploadResultReceived += OnPlcUploadResultReceived;

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
            .SelectMany(group =>
            {
                if (BarcodeMode == BarcodeMode.MultiBarcode)
                {
                    // 多条码模式：使用 Buffer 进行配对，有 500ms 超时
                    return group
                        .Buffer(TimeSpan.FromMilliseconds(500), 2)
                        .Where(buffer => buffer.Count > 0);
                }

                // 母/子条码模式：立即处理每个包裹，不等待
                // 将单个包裹包装成列表以匹配订阅者期望的类型 (IList<PackageInfo>)
                return group.Select(p => (IList<PackageInfo>)[p]);
            })
            .ObserveOn(Scheduler.Default) // 在后台线程处理 Buffer 结果
            .SelectMany(buffer =>
            {
                try
                {
                    var firstPackage = buffer[0];
                    var currentPrefix = GetBarcodePrefix(firstPackage.Barcode);
                    var initialPackageContext = $"[临时|{currentPrefix}]"; // 用于配对/超时阶段的临时上下文

                    // *** Keep LogContext for the initial buffering/pairing phase ***
                    using (LogContext.PushProperty("PackageContext", initialPackageContext))
                    {
                        Log.Debug(
                            "[Stream][Buffer] 处理 Buffer (Count: {Count}). Prefix='{Prefix}', First Idx={Index}, First Barcode='{Barcode}'",
                            buffer.Count, currentPrefix, firstPackage.Index, firstPackage.Barcode);

                        // ... (existing timeout check logic using initialPackageContext) ...
                        if (_timedOutPrefixes.TryGetValue(currentPrefix, out var timeoutTime))
                        {
                            if (DateTime.UtcNow - timeoutTime < TimedOutPrefixMaxAge)
                            {
                                if (buffer.Count == 1) // 迟到的包裹
                                {
                                    Log.Warning("[Stream][Discard] 丢弃迟到的单个包裹 (配对已超时): Idx={Index}, Barcode='{Barcode}'",
                                        firstPackage.Index, firstPackage.Barcode);
                                    firstPackage.ReleaseImage();
                                    _timedOutPrefixes.TryRemove(currentPrefix, out _); // 收到后移除标记
                                    return Task.FromResult<IList<PackageInfo>[]>([]);
                                }

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
                                // ... (existing merge/filter logic using initialPackageContext logging) ...
                                case 2 when BarcodeMode == BarcodeMode.MultiBarcode:
                                    var p1 = buffer.FirstOrDefault(p => !IsParentBarcode(p.Barcode)) ?? buffer[0];
                                    var p2 = buffer.FirstOrDefault(p => IsParentBarcode(p.Barcode)) ?? buffer[1];
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

                                // *** Remove LogContext push here ***
                                // *** Log the reception explicitly ***
                                Log.Information("{Context} [接收] 包裹进入处理流程", finalPackageContext);

                                // *** Pass the context string explicitly ***
                                FetchWeightAndHandlePackageAsync(packageToProcess, finalPackageContext);
                            }
                        }
                        catch (Exception ex)
                        {
                            // Log error using initialPackageContext
                            Log.Error(ex, "[Stream] 处理 Buffer (Prefix='{Prefix}', Count={Count}) 时发生内部错误.",
                                currentPrefix, buffer.Count);
                            // 尝试释放 buffer 中的图像
                            foreach (var pkg in buffer) pkg.ReleaseImage();
                        }
                    } // 结束 initialPackageContext using
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[Stream] 包裹流处理中发生未处理的异步异常");
                }

                // SelectMany需要返回一个序列，这里返回空序列表示处理完成
                return Task.FromResult(Array.Empty<IList<PackageInfo>>());
            })
            .Subscribe(_ => { }, // 忽略SelectMany的结果
                ex => Log.Error(ex, "[Stream] 包裹流处理中发生未处理的顶层异常"))); // 流的顶层错误处理

        // +++ 订阅来自 ICameraService 的带 ID 图像流 +++ (优化版：节流 + 缓存 + 后台处理)
        _subscriptions.Add(_cameraService.ImageStreamWithId
            .ObserveOn(Scheduler.Default) // 在UI线程外处理转换
            .GroupBy(data => data.CameraId) // 按相机ID分组
            .SelectMany(group => group
                .ObserveOn(Scheduler.Default)) // 继续在后台处理节流结果
            .SelectMany(async imageData =>
            {
                var cameraId = imageData.CameraId;
                var image = imageData.Image;

                Log.Verbose("[Camera] 接收到图像数据: CameraId={CameraId}, ImageSize={Width}x{Height}, ImageFormat={Format}",
                    cameraId, image.PixelWidth, image.PixelHeight, image.Format.ToString());

                try
                {
                    // 后台线程：异步准备图像（仅克隆和冻结）
                    var processedImage = await Task.Run(() =>
                    {
                        try
                        {
                            // 克隆并冻结图像（保持原始尺寸）
                            var safeImage = image.Clone();
                            if (safeImage.CanFreeze)
                            {
                                safeImage.Freeze();
                                Log.Debug("图像冻结成功: CameraId={CameraId}, Size={Width}x{Height}",
                                    cameraId, safeImage.PixelWidth, safeImage.PixelHeight);
                            }
                            else
                            {
                                Log.Warning("图像无法冻结: CameraId={CameraId}, Size={Width}x{Height}",
                                    cameraId, safeImage.PixelWidth, safeImage.PixelHeight);
                            }

                            return safeImage;
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "后台处理图像时出错: CameraId={CameraId}", cameraId);
                            return null;
                        }
                    });

                    if (processedImage == null)
                    {
                        Log.Warning("相机 {CameraId} 图像处理失败，跳过UI更新", cameraId);
                        return [];
                    }

                    // UI线程：快速更新UI（只做必要的查找和赋值）
                    await DispatchInvokeAsyncFireAndForget(() =>
                    {
                        try
                        {
                            // 记录当前相机列表状态
                            Log.Debug("[Camera] 当前相机列表: {CameraList}",
                                string.Join(", ", Cameras.Select(c => $"{c.CameraId}({c.CameraName})")));

                            var targetCamera = Cameras.FirstOrDefault(c => c.CameraId == cameraId);
                            if (targetCamera == null)
                            {
                                Log.Warning("未找到相机ID={CameraId}的显示区域，可用相机: {AvailableCameras}",
                                    cameraId, string.Join(", ", Cameras.Select(c => c.CameraId)));
                                return;
                            }

                            // 记录图像更新
                            var oldImage = targetCamera.CurrentImage;
                            targetCamera.CurrentImage = processedImage;

                            Log.Debug("相机图像更新成功: CameraId={CameraId}, 旧图像={OldImageExists}, 新图像大小={Width}x{Height}",
                                cameraId, oldImage != null, processedImage.PixelWidth, processedImage.PixelHeight);
                        }
                        catch (Exception uiEx)
                        {
                            Log.Error(uiEx, "UI线程更新相机图像时出错: CameraId={CameraId}", cameraId);
                        }
                    }, DispatcherPriority.Render); // 使用Render优先级，确保UI流畅
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理相机图像流时发生错误: CameraId={CameraId}", cameraId);
                }

                // SelectMany需要返回一个序列，这里返回空序列
                return Array.Empty<(BitmapSource Image, string CameraId)>();
            })
            .Subscribe(
                _ => { }, // 忽略SelectMany的结果
                ex => Log.Error(ex, "[Camera] 图像流处理中发生未处理的顶层异常"),
                () => Log.Information("[Camera] 图像流处理完成"))); // 图像流的顶层错误处理

        // *** 初始化 IsNextPackageWaiting ***
        IsNextPackageWaiting = !_packageStack.IsEmpty;

        // 将初始背景设置为绿色
        MainWindowBackgroundBrush = BackgroundSuccess;

        // 检查相机服务是否已经连接，如果是则立即填充相机列表
        if (!_cameraService.IsConnected || _camerasInitialized) return;
        Log.Information("相机服务已连接，立即填充相机列表");
        PopulateCameraList();
    }

    // Helper: 安全地在 UI 线程上异步执行一个无返回值的操作并记录异常
    // 返回 Task 以便调用者可选择 await 或 fire-and-forget
    private static Task DispatchInvokeAsyncFireAndForget(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
    {
        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
            {
                // 无法调度到 UI 线程，直接运行（谨慎）
                try { action(); } catch (Exception ex) { Log.Error(ex, "DispatchInvokeAsyncFireAndForget: 直接执行动作失败"); }
                return Task.CompletedTask;
            }

            // 使用 InvokeAsync 并返回其 Task，调用者可以 await 或忽略
            var op = dispatcher.InvokeAsync(() =>
            {
                try { action(); }
                catch (Exception ex) { Log.Error(ex, "DispatchInvokeAsyncFireAndForget: UI 操作抛出异常"); }

            }, priority);

            // Attach continuation to observe exceptions from the DispatcherOperation if any
            op.Task.ContinueWith(t =>
            {
                if (t is { IsFaulted: true, Exception: not null })
                    Log.Error(t.Exception, "DispatchInvokeAsyncFireAndForget: 调度任务失败");
            }, TaskContinuationOptions.OnlyOnFaulted);

            return op.Task;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DispatchInvokeAsyncFireAndForget: 包装调度调用失败");
            return Task.CompletedTask;
        }
    }

    /// <summary>
    ///     指示是否有包裹正在队列中等待PLC处理
    /// </summary>
    public bool IsNextPackageWaiting
    {
        get => _isNextPackageWaiting;
        private set => SetProperty(ref _isNextPackageWaiting, value);
    }

    public DelegateCommand OpenSettingsCommand { get; }
    public ObservableCollection<PackageInfo> PackageHistory { get; } = [];
    public ObservableCollection<StatisticsItem> StatisticsItems { get; } = [];
    public ObservableCollection<DeviceStatusInfo> DeviceStatuses { get; private set; } = [];
    public ObservableCollection<PackageInfoItem> PackageInfoItems { get; } = [];

    public ObservableCollection<CameraDisplayInfo> Cameras { get; } = [];

    // PLC状态独立属性
    public string PlcStatusText
    {
        get => _plcStatusText;
        private set => SetProperty(ref _plcStatusText, value);
    }

    public string PlcStatusDescription
    {
        get => _plcStatusDescription;
        private set => SetProperty(ref _plcStatusDescription, value);
    }

    public string PlcStatusColor
    {
        get => _plcStatusColor;
        private set => SetProperty(ref _plcStatusColor, value);
    }

    // JD WCS状态独立属性
    public string JdWcsStatusText
    {
        get => _jdWcsStatusText;
        private set => SetProperty(ref _jdWcsStatusText, value);
    }

    public string JdWcsStatusDescription
    {
        get => _jdWcsStatusDescription;
        private set => SetProperty(ref _jdWcsStatusDescription, value);
    }

    public string JdWcsStatusColor
    {
        get => _jdWcsStatusColor;
        private set => SetProperty(ref _jdWcsStatusColor, value);
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


    /// <summary>
    ///     指示上一个处理的包裹是否成功
    /// </summary>
    public bool LastPackageWasSuccessful
    {
        get => _lastPackageWasSuccessful;
        private set => SetProperty(ref _lastPackageWasSuccessful, value);
    }

    /// <summary>
    ///     主窗口内容区域背景画刷 - 控制上包状态 (黄/绿)
    /// </summary>
    public Brush MainWindowBackgroundBrush
    {
        get => _mainWindowBackgroundBrush;
        private set => SetProperty(ref _mainWindowBackgroundBrush, value);
    }
    

    /// <summary>
    ///     控制PLC异常警告覆盖层的可见性
    /// </summary>
    public bool IsPlcAbnormalWarningVisible
    {
        get => _isPlcAbnormalWarningVisible;
        private set => SetProperty(ref _isPlcAbnormalWarningVisible, value);
    }

    /// <summary>
    ///     条码模式
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
    ///     用于绑定 ComboBox.SelectedIndex 的属性
    /// </summary>
    public int SelectedBarcodeModeIndex
    {
        get => _selectedBarcodeModeIndex;
        set
        {
            // 使用 SetProperty 检查值是否真的改变
            if (SetProperty(ref _selectedBarcodeModeIndex, value))
                // 当 Index 改变时，更新 BarcodeMode 枚举属性
                // BarcodeMode 的 setter 会处理日志记录和保存设置
                BarcodeMode = (BarcodeMode)value;
        }
    }

    /// <summary>
    ///     网格的行数
    /// </summary>
    public int GridRows
    {
        get => _gridRows;
        private set => SetProperty(ref _gridRows, value);
    }

    /// <summary>
    ///     网格的列数
    /// </summary>
    public int GridColumns
    {
        get => _gridColumns;
        private set => SetProperty(ref _gridColumns, value);
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

    /// <summary>
    ///     是否正在等待PLC上包确认结果
    /// </summary>
    public bool IsWaitingForUploadResult
    {
        get => _isWaitingForUploadResult;
        private set => SetProperty(ref _isWaitingForUploadResult, value);
    }

    /// <summary>
    ///     等待倒计时秒数
    /// </summary>
    public int WaitingCountdownSeconds
    {
        get => _waitingCountdownSeconds;
        private set => SetProperty(ref _waitingCountdownSeconds, value);
    }

    /// <summary>
    ///     等待倒计时圆环进度 (0-100)
    /// </summary>
    public double WaitingCountdownProgress
    {
        get => _waitingCountdownProgress;
        private set => SetProperty(ref _waitingCountdownProgress, value);
    }

    /// <summary>
    ///     等待状态文本
    /// </summary>
    public string WaitingStatusText
    {
        get => _waitingStatusText;
        private set => SetProperty(ref _waitingStatusText, value);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     获取条码模式的显示文本
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
    ///     根据当前设置的条码模式过滤包裹
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
            Log.Debug("[Filter] 包裹因模式 {Mode} 与条码类型 (IsParent: {IsParent}) 不符被过滤: {Barcode}",
                BarcodeMode, isParentBarcode, package.Barcode);

        return pass;
    }

    /// <summary>
    ///     初始化相机占位符
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
    ///     手动刷新相机显示（用于解决显示问题）
    /// </summary>
    public void RefreshCameraDisplay()
    {
        try
        {
            // 添加线程安全检查
            if (_disposed)
            {
                Log.Warning("[CameraRefresh] ViewModel已释放，跳过相机刷新");
                return;
            }

            Log.Information("[CameraRefresh] 开始手动刷新相机显示");

            // 重置初始化标志，强制重新初始化
            _camerasInitialized = false;

            // 清空当前相机列表
            Cameras.Clear();

            // 重新初始化相机占位符
            InitializeCameraPlaceholder();

            // 如果相机服务已连接，重新填充相机列表
            if (_cameraService.IsConnected)
            {
                Log.Information("[CameraRefresh] 相机服务已连接，重新填充相机列表");
                PopulateCameraList();
            }
            else
            {
                Log.Warning("[CameraRefresh] 相机服务未连接，等待连接后自动刷新");
            }

            Log.Information("[CameraRefresh] 相机显示刷新完成");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CameraRefresh] 刷新相机显示时发生错误");
        }
    }

    /// <summary>
    ///     调试相机状态（用于排查显示问题）
    /// </summary>
    private void DebugCameraStatus()
    {
        try
        {
            if (_disposed)
            {
                Log.Warning("[CameraDebug] ViewModel已释放，跳过相机状态调试");
                return;
            }

            Log.Information("[CameraDebug] === 相机状态调试信息 ===");
            Log.Information("[CameraDebug] 相机服务连接状态: {Connected}", _cameraService.IsConnected);
            Log.Information("[CameraDebug] 相机初始化状态: {Initialized}", _camerasInitialized);
            Log.Information("[CameraDebug] 相机列表数量: {Count}", Cameras.Count);

            foreach (var camera in Cameras)
            {
                try
                {
                    Log.Information("[CameraDebug] 相机信息: ID={Id}, 名称={Name}, 在线={Online}, 图像={HasImage}, 图像大小={Size}",
                        camera.CameraId,
                        camera.CameraName,
                        camera.IsOnline,
                        camera.CurrentImage != null,
                        camera.CurrentImage != null ? $"{camera.CurrentImage.PixelWidth}x{camera.CurrentImage.PixelHeight}" : "无");
                }
                catch (Exception cameraEx)
                {
                    Log.Error(cameraEx, "[CameraDebug] 调试相机信息时发生错误: CameraId={CameraId}", camera.CameraId);
                }
            }

            // 检查相机服务的可用相机
            try
            {
                if (_cameraService.IsConnected)
                {
                    var availableCameras = _cameraService.GetAvailableCameras().ToList();
                    Log.Information("[CameraDebug] 服务报告的可用相机数量: {Count}", availableCameras.Count);
                    foreach (var cam in availableCameras)
                    {
                        Log.Information("[CameraDebug] 可用相机: ID={Id}, 名称={Name}", cam.Id, cam.Name);
                    }
                }
                else
                {
                    Log.Information("[CameraDebug] 相机服务未连接，跳过可用相机检查");
                }
            }
            catch (Exception serviceEx)
            {
                Log.Error(serviceEx, "[CameraDebug] 检查相机服务可用相机时发生错误");
            }

            Log.Information("[CameraDebug] === 调试信息结束 ===");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CameraDebug] 调试相机状态时发生错误");
        }
    }

    /// <summary>
    ///     填充相机列表（在相机服务连接成功后调用）
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
                        ColumnSpan = columnSpan
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
        try
        {
            Log.Information("[Camera] 相机连接状态变更: CameraId={CameraId}, IsConnected={Connected}",
                cameraId ?? "ALL", isConnected);

            _ = DispatchInvokeAsyncFireAndForget(() =>
            {
                try
                {
                    // 检查ViewModel是否已被释放
                    if (_disposed)
                    {
                        Log.Debug("[Camera] ViewModel已释放，跳过相机状态更新");
                        return;
                    }

                    var cameraStatus = DeviceStatuses.FirstOrDefault(static s => s.Name == "相机");
                    if (cameraStatus != null)
                    {
                        cameraStatus.Status = isConnected ? "已连接" : "已断开";
                        cameraStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
                    }

                    // 首次整体连接成功时，填充相机列表
                    if (string.IsNullOrEmpty(cameraId) && isConnected && !_camerasInitialized)
                    {
                        try
                        {
                            Log.Information("[Camera] 相机服务整体连接成功，开始填充相机列表");
                            PopulateCameraList();

                            // 连接成功后执行一次状态调试
                            Task.Delay(1000).ContinueWith(_ =>
                            {
                                try
                                {
                                    DebugCameraStatus();
                                }
                                catch (Exception debugEx)
                                {
                                    Log.Error(debugEx, "[Camera] 连接成功后的状态调试失败");
                                }
                            });
                        }
                        catch (Exception populateEx)
                        {
                            Log.Error(populateEx, "[Camera] 填充相机列表失败");
                        }
                        return;
                    }

                    // 处理相机断开连接
                    if (!isConnected && string.IsNullOrEmpty(cameraId))
                    {
                        try
                        {
                            Log.Information("[Camera] 相机服务整体断开，清空所有相机状态");
                            foreach (var cam in Cameras)
                            {
                                cam.IsOnline = false;
                                cam.CurrentImage = null; // 清空图像
                            }
                        }
                        catch (Exception clearEx)
                        {
                            Log.Error(clearEx, "[Camera] 清空相机状态失败");
                        }
                        return;
                    }

                    // 更新单个相机状态
                    if (string.IsNullOrEmpty(cameraId)) return;

                    try
                    {
                        var found = false;
                        foreach (var cam in Cameras)
                        {
                            if (cam.CameraId != cameraId) continue;
                            cam.IsOnline = isConnected;
                            if (!isConnected)
                            {
                                cam.CurrentImage = null; // 相机断开时清空图像
                            }
                            Log.Information("[Camera] 更新相机 {ID} 状态为: {Status}", cameraId, isConnected ? "在线" : "离线");
                            found = true;
                            break;
                        }

                        if (!found)
                        {
                            Log.Warning("[Camera] 未找到ID为 {CameraId} 的相机，无法更新状态", cameraId);
                        }
                    }
                    catch (Exception updateEx)
                    {
                        Log.Error(updateEx, "[Camera] 更新单个相机状态失败: CameraId={CameraId}", cameraId);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[Camera] 处理相机连接状态变更时发生错误: CameraId={CameraId}, IsConnected={Connected}",
                        cameraId ?? "ALL", isConnected);
                }
            });
        }
        catch (Exception dispatchEx)
        {
            Log.Error(dispatchEx, "[Camera] 调度相机连接状态变更处理失败: CameraId={CameraId}, IsConnected={Connected}",
                cameraId ?? "ALL", isConnected);
        }
    }

    private void ExecuteOpenSettings()
    {
        try
        {
            if (_disposed)
            {
                Log.Warning("ViewModel已释放，跳过打开设置对话框");
                return;
            }

            _dialogService.ShowDialog("SettingsDialog");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "打开设置对话框时发生错误");
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        try
        {
            if (_disposed)
            {
                Log.Debug("[Timer] ViewModel已释放，停止定时器更新");
                _timer.Stop();
                return;
            }

            SystemStatus = SystemStatus.GetCurrentStatus();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Timer] 系统状态更新时发生错误");
        }
    }

    private void InitializeDeviceStatuses()
    {
        DeviceStatuses =
        [
            new DeviceStatusInfo("相机", "Camera24", _cameraService.IsConnected ? "已连接" : "已断开",
                _cameraService.IsConnected ? "#4CAF50" : "#F44336"),
            new DeviceStatusInfo("PLC", "Router24", _plcCommunicationService.IsConnected ? "正常" : "未连接",
                _plcCommunicationService.IsConnected ? "#4CAF50" : "#F44336"), // Added PLC status
            new DeviceStatusInfo("JD WCS", "CloudDatabase24", _jdWcsCommunicationService.IsConnected ? "已连接" : "未连接",
                _jdWcsCommunicationService.IsConnected ? "#4CAF50" : "#F44336") // Added JD WCS status
        ];
        // 初始更新合并PLC状态文本
        OnPlcDeviceStatusChanged(this,
            _plcCommunicationService.IsConnected ? DeviceStatusCode.Normal : DeviceStatusCode.Disconnected);
    }

    private void InitializeIndividualDeviceStatuses()
    {
        // 初始化PLC状态
        var plcStatusCode = _plcCommunicationService.IsConnected
            ? DeviceStatusCode.Normal
            : DeviceStatusCode.Disconnected;
        PlcStatusText = GetDeviceStatusDisplayText(plcStatusCode);
        PlcStatusDescription = GetDeviceStatusDescription(plcStatusCode);
        PlcStatusColor = GetDeviceStatusColor(plcStatusCode);

        // 初始化JD WCS状态
        var isJdWcsConnected = _jdWcsCommunicationService.IsConnected;
        JdWcsStatusText = isJdWcsConnected ? "已连接" : "未连接";
        JdWcsStatusDescription = isJdWcsConnected ? "京东WCS服务连接正常，可以上传数据" : "京东WCS服务未连接，请检查网络连接";
        JdWcsStatusColor = isJdWcsConnected ? "#4CAF50" : "#F44336";
    }

    private void InitializeStatisticsItems()
    {
        StatisticsItems.Add(new StatisticsItem("总包裹数", "0", "个", "累计处理包裹总数", "BoxMultiple24"));
        StatisticsItems.Add(new StatisticsItem("成功数", "0", "个", "处理成功的包裹数量", "CheckmarkCircle24"));
        StatisticsItems.Add(new StatisticsItem("失败数", "0", "个", "处理失败的包裹数量", "ErrorCircle24"));
        StatisticsItems.Add(new StatisticsItem("处理速率", "0", "个/小时", "每小时处理包裹数量", "ArrowTrendingLines24"));
    }

    private void InitializePackageInfoItems()
    {
        PackageInfoItems.Add(new PackageInfoItem("重量", "0.00", "kg", "包裹重量", "Scales24"));
        PackageInfoItems.Add(new PackageInfoItem("尺寸", "0 × 0 × 0", "cm", "长 × 宽 × 高", "Ruler24"));
        PackageInfoItems.Add(new PackageInfoItem("时间", "--:--:--", "", "处理时间", "Timer24"));
        PackageInfoItems.Add(new PackageInfoItem("状态", "等待扫码", "", "等待 PLC 指令或扫码", "Alert24"));
    }


    // *** 添加: 处理传入包裹的新入口 ***
    // *** Add packageContext parameter ***
    private void HandleIncomingPackage(PackageInfo package, string packageContext)
    {
        // *** Use passed context for logging ***
        Log.Debug("{Context} 进入 HandleIncomingPackage (调度逻辑)", packageContext);
        lock (_processingLock)
        {
            if (_currentlyProcessingPackage == null)
            {
                _currentlyProcessingPackage = package;
                Log.Information("{Context} [调度] 系统空闲，开始处理", packageContext);
                // *** Pass context ***
                _ = ProcessSinglePackageAsync(_currentlyProcessingPackage, packageContext, _viewModelCts.Token);
            }
            else
            {
                // 检查堆栈是否已满（只保留一个待处理包裹）
                if (!_packageStack.IsEmpty)
                {
                    // 堆栈已满，直接跳过新包裹
                    var currentProcessingContext = _currentlyProcessingPackage.Index >= 0
                        ? $"[包裹{_currentlyProcessingPackage.Index}|{_currentlyProcessingPackage.Barcode}]"
                        : "[未知处理中包裹]";
                    Log.Warning("{NewContext} [调度] 系统正忙且堆栈已满 (处理中: {CurrentContext}, 堆栈大小: {StackCount}), 跳过新包裹",
                        packageContext, currentProcessingContext, _packageStack.Count);

                    // 释放跳过包裹的图像资源
                    package.ReleaseImage();
                    return; // 直接返回，不再处理
                }

                // 堆栈为空，将新包裹放入堆栈
                var processingContext = _currentlyProcessingPackage.Index >= 0
                    ? $"[包裹{_currentlyProcessingPackage.Index}|{_currentlyProcessingPackage.Barcode}]"
                    : "[未知处理中包裹]";
                Log.Information("{NewContext} [调度] 系统正忙 (处理中: {CurrentContext}), 将新包裹放入堆栈等待",
                    packageContext, processingContext);
                _packageStack.Push(package);
                IsNextPackageWaiting = true;

                // 确保UI状态为忙碌状态
                _ = DispatchInvokeAsyncFireAndForget(() =>
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

    // *** 新增: 强制释放处理权限的方法 - 用于异常恢复 ***
    private void ForceReleaseProcessingLock(string packageContext)
    {
        Log.Warning("{Context} [强制释放] 开始强制释放处理权限", packageContext);

        try
        {
            lock (_processingLock)
            {
                // 强制设置当前处理包裹为空
                _currentlyProcessingPackage = null;

                // 检查堆栈并尝试处理下一个包裹
                if (_packageStack.TryPop(out var nextPackage))
                {
                    var nextContext = $"[包裹{nextPackage.Index}|{nextPackage.Barcode}]";
                    Log.Warning("{Context} [强制释放] 发现堆栈中有待处理包裹: {NextContext}", packageContext, nextContext);

                    _currentlyProcessingPackage = nextPackage;
                    IsNextPackageWaiting = !_packageStack.IsEmpty;

                    // 在锁外启动下一个处理
                    Task.Run(async () =>
                    {
                        try
                        {
                            await ProcessSinglePackageAsync(nextPackage, nextContext, _viewModelCts.Token);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "{Context} [强制释放] 启动下一个包裹处理时发生异常", nextContext);
                        }
                    });
                }
                else
                {
                    IsNextPackageWaiting = false;
                    // 设置UI为空闲状态
                    _ = DispatchInvokeAsyncFireAndForget(() =>
                    {
                        try
                        {
                            if (MainWindowBackgroundBrush != BackgroundSuccess)
                            {
                                MainWindowBackgroundBrush = BackgroundSuccess;
                                Log.Information("[状态][UI] [强制释放] 设置背景为绿色 (允许上包) - 系统空闲");
                            }
                        }
                        catch (Exception uiEx)
                        {
                            Log.Error(uiEx, "[强制释放] 更新UI状态时发生异常");
                        }
                    });
                }
            }

            Log.Information("{Context} [强制释放] 处理权限已强制释放", packageContext);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Context} [强制释放] 强制释放处理权限时发生异常", packageContext);
        }
    }

    // *** 添加: 处理单个包裹的方法 ***
    // *** Add packageContext parameter ***
    private async Task ProcessSinglePackageAsync(PackageInfo package, string packageContext,
        CancellationToken cancellationToken)
    {
        // Ensure LogContext is pushed for the entire duration of processing this package
        using (LogContext.PushProperty("PackageContext", packageContext))
        {
            Log.Information("{Context} 开始核心处理流程", packageContext);

            try // New outer try block to guarantee finally execution
            {
                // Entering processing logic means increment total packages (only here +1)
                IncrementTotalPackages();

                // 1. Update UI status (disallow upload, show basic info)
                _ = DispatchInvokeAsyncFireAndForget(() =>
                {
                    if (MainWindowBackgroundBrush != BackgroundTimeout)
                    {
                        MainWindowBackgroundBrush = BackgroundTimeout;
                        Log.Information("[状态][UI] 设置背景为 黄色 (禁止上包) - 处理开始");
                    }

                    CurrentBarcode = package.Barcode;
                    UpdatePackageInfoItemsBasic(package); // Update weight, dimensions, time
                    var statusItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "状态");
                    if (statusItem == null) return;
                    statusItem.Value = $"处理中 (序号: {package.Index})";
                    statusItem.Description = "检查PLC状态..."; // Update description
                    statusItem.StatusColor = "#FFC107"; // Yellow
                });

                // --- Initial Check ---
                if (cancellationToken.IsCancellationRequested)
                {
                    Log.Warning("处理在开始时被取消.");
                    package.ReleaseImage();
                    package.SetStatus(PackageStatus.Error, "操作取消 (开始时)");
                    // *** 确保释放处理权限 ***
                    try { FinalizeProcessing(package, packageContext); }
                    catch (Exception ex) { Log.Error(ex, "{Context} 释放处理权限时出错", packageContext); }
                    return;
                }

                // Check PLC Status
                if (PlcStatusText != "正常") // Directly check updated status text
                {
                    Log.Warning("PLC状态异常 ({StatusText})，无法处理.", PlcStatusText);
                    package.SetStatus(PackageStatus.Error, $"PLC状态异常: {PlcStatusText}");
                    _ = DispatchInvokeAsyncFireAndForget(() => UpdateUiFromResult(package));
                    _ = _audioService.PlayPresetAsync(AudioType.PlcDisconnected);
                    package.ReleaseImage();
                    // *** 确保释放处理权限 ***
                    try { FinalizeProcessing(package, packageContext); }
                    catch (Exception ex) { Log.Error(ex, "{Context} 释放处理权限时出错", packageContext); }
                    return;
                }

                if (IsPlcAbnormalWarningVisible) IsPlcAbnormalWarningVisible = false; // Status normal, hide warning

                // --- Send Upload Request ---
                try
                {
                    _ = DispatchInvokeAsyncFireAndForget(() =>
                    {
                        var statusItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "状态");
                        if (statusItem != null) statusItem.Description = "请求PLC上包...";
                    });

                    Log.Information("向PLC发送上传请求: W={Weight:F3}, L={L:F1}, W={W:F1}, H={H:F1}",
                        package.Weight, package.Length ?? 0, package.Width ?? 0, package.Height ?? 0);

                    var plcRequestTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                    // 1. Send request and wait for ACK (2 second timeout)
                    (bool IsAccepted, ushort CommandId) ackResult;
                    try
                    {
                        _ = DispatchInvokeAsyncFireAndForget(() =>
                        {
                            var statusItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "状态");
                            if (statusItem == null) return;
                            statusItem.Value = $"等待PLC确认 (序号: {package.Index})";
                            statusItem.Description = "等待PLC确认接受...";
                            statusItem.StatusColor = "#FFC107"; // Yellow
                        });

                        Log.Debug("等待PLC ACK...");

                        Log.Information(
                            "准备调用 SendUploadRequestAsync: Barcode='{Barcode}', W={Weight:F3}, L={L:F1}, W={W:F1}, H={H:F1}, Timestamp={Ts}",
                            package.Barcode, package.Weight, package.Length ?? 0, package.Width ?? 0,
                            package.Height ?? 0, plcRequestTimestamp);

                        // Create 2-second timeout cancellation token
                        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        using var linkedCts =
                            CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

                        ackResult = await _plcCommunicationService.SendUploadRequestAsync(
                            (float)package.Weight, (float)(package.Length ?? 0), (float)(package.Width ?? 0),
                            (float)(package.Height ?? 0),
                            package.Barcode, string.Empty, (ulong)plcRequestTimestamp, linkedCts.Token);
                    }
                    catch (OperationCanceledException ex) when (ex.CancellationToken.IsCancellationRequested)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            Log.Warning("等待PLC ACK时操作被取消.");
                            package.SetStatus(PackageStatus.Error, "操作取消 (等待PLC确认)");
                        }
                        else
                        {
                            Log.Warning("等待PLC ACK超时（2秒），视为拒绝上包.");
                            package.SetStatus(PackageStatus.LoadingRejected, $"上包拒绝 (超时) (序号: {package.Index})");
                            _ = _audioService.PlayPresetAsync(AudioType.LoadingRejected);
                            Log.Information("上包ACK超时，标记结果，等待统一统计");
                        }
                        _ = DispatchInvokeAsyncFireAndForget(() => MainWindowBackgroundBrush = BackgroundSuccess);
                        // *** 确保释放处理权限 ***
                        try { FinalizeProcessing(package, packageContext); }
                        catch (Exception finalizeEx) { Log.Error(finalizeEx, "{Context} 释放处理权限时出错", packageContext); }
                        return;
                    }
                    catch (Exception ackEx)
                    {
                        Log.Error(ackEx, "{Context} 发送PLC请求或等待ACK时出错", packageContext);
                        package.SetStatus(PackageStatus.Error, $"PLC通信错误 (ACK): {ackEx.Message}");

                        // 确保UI状态正确设置
                        try
                        {
                            _ = DispatchInvokeAsyncFireAndForget(() => MainWindowBackgroundBrush = BackgroundSuccess);
                        }
                        catch (Exception uiEx)
                        {
                            Log.Error(uiEx, "{Context} 设置UI背景为成功状态时发生异常", packageContext);
                        }

                        // 确保倒计时被停止
                        try { StopUploadCountdown(); }
                        catch (Exception countdownEx)
                        {
                            Log.Error(countdownEx, "{Context} 停止上传倒计时失败", packageContext);
                        }

                        // *** 确保释放处理权限 ***
                        try { FinalizeProcessing(package, packageContext); }
                        catch (Exception finalizeEx) { Log.Error(finalizeEx, "{Context} 释放处理权限时出错", packageContext); }
                        return;
                    }

                    // --- Process ACK Result ---
                    if (!ackResult.IsAccepted)
                    {
                        Log.Warning("PLC拒绝上包请求. CommandId={CommandId}", ackResult.CommandId);
                        package.SetStatus(PackageStatus.LoadingRejected, $"上包拒绝 (序号: {package.Index})");
                        _ = _audioService.PlayPresetAsync(AudioType.LoadingRejected);
                        StopUploadCountdown();
                        _ = DispatchInvokeAsyncFireAndForget(() =>
                        {
                            MainWindowBackgroundBrush = BackgroundSuccess;
                            Log.Information("[状态][UI] 设置背景为 绿色 (允许上包) - PLC拒绝");
                        });
                        // *** 确保释放处理权限 ***
                        try { FinalizeProcessing(package, packageContext); }
                        catch (Exception finalizeEx) { Log.Error(finalizeEx, "{Context} 释放处理权限时出错", packageContext); }
                        return;
                    }

                    // --- PLC Accepted ---
                    Log.Information("PLC接受上包请求. CommandId={CommandId}", ackResult.CommandId);

                    _ = _audioService.PlayPresetAsync(AudioType.PleasePlacePackage);

                    _ = DispatchInvokeAsyncFireAndForget(() =>
                    {
                        MainWindowBackgroundBrush = BackgroundSuccess;
                        Log.Information("[状态][UI] 设置背景为 绿色 (允许上包) - PLC接受");
                        var statusItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "状态");
                        if (statusItem == null) return;
                        statusItem.Value = $"等待上包完成 (序号: {package.Index})";
                        statusItem.Description = "PLC已接受，等待上包完成...";
                        statusItem.StatusColor = "#FFC107"; // Yellow - waiting
                    });

                    // --- Wait for PLC Final Result ---
                    Log.Information("开始等待PLC上包最终结果...");

                    var config = _settings_service.LoadSettings<HostConfiguration>();
                    var resultTimeoutSeconds = config.UploadResultTimeoutSeconds;

                    Log.Debug("准备启动等待PLC确认倒计时: {TimeoutSeconds}秒", resultTimeoutSeconds);
                    StartWaitingCountdown(resultTimeoutSeconds);

                    try
                    {
                        using var finalResultTimeoutCts =
                            new CancellationTokenSource(TimeSpan.FromSeconds(resultTimeoutSeconds));
                        using var finalResultLinkedCts =
                            CancellationTokenSource.CreateLinkedTokenSource(finalResultTimeoutCts.Token, cancellationToken);

                        var (wasSuccess, isTimeout, packageId) = await _plcCommunicationService.WaitForUploadResultAsync(
                            ackResult.CommandId, finalResultLinkedCts.Token);

                        if (isTimeout)
                        {
                            Log.Warning("等待PLC上包最终结果超时");
                            package.SetStatus(PackageStatus.LoadingTimeout, $"上包超时 (序号: {package.Index})");
                            _ = _audioService.PlayPresetAsync(AudioType.LoadingTimeout);
                        }
                        else if (wasSuccess)
                        {
                            Log.Information("PLC上包成功. PackageId={PackageId}", packageId);
                            package.SetStatus(PackageStatus.LoadingSuccess, $"上包成功 (序号: {package.Index})");
                            _ = _audioService.PlayPresetAsync(AudioType.LoadingSuccess);

                            // *** 优化: 图像处理和上传改为异步执行，不阻塞主流程 ***
                            // 先克隆一份图像以避免后台处理期间主流程释放原始图像导致丢失
                            BitmapSource? clonedImage = null;
                            try
                            {
                                if (package.Image != null)
                                {
                                    clonedImage = package.Image.Clone();
                                    if (clonedImage.CanFreeze) clonedImage.Freeze();
                                }
                            }
                            catch (Exception cloneEx)
                            {
                                Log.Warning(cloneEx, "{Context} 克隆图像用于异步处理失败，后台处理将尝试再次克隆", packageContext);
                                clonedImage = null;
                            }

                            // 将图像处理任务添加到队列 - 使用更高效的fire-and-forget方式
                            _ = DispatchInvokeAsyncFireAndForget(async void () =>
                            {
                                try
                                {
                                    await QueueImageProcessingTaskAsync(package, packageContext, packageId, clonedImage);
                                }
                                catch (Exception queueEx)
                                {
                                    Log.Error(queueEx, "{Context} 将图像处理任务添加到队列时发生异常: PackageId={PackageId}",
                                        packageContext, packageId);

                                    // 如果队列添加失败，尝试异步更新状态显示
                                    try
                                    {
                                        await DispatchInvokeAsyncFireAndForget(() =>
                                        {
                                            try
                                            {
                                                var currentDisplay = package.StatusDisplay;
                                                package.SetStatus(package.Status, $"{currentDisplay} [队列异常]");
                                            }
                                            catch (Exception uiEx)
                                            {
                                                Log.Error(uiEx, "{Context} 异步更新队列异常状态时发生UI异常", packageContext);
                                            }
                                        });
                                    }
                                    catch (Exception dispatcherEx)
                                    {
                                        Log.Error(dispatcherEx, "{Context} 异步更新队列异常状态时发生调度异常", packageContext);
                                    }
                                }
                            });
                        }
                        else
                        {
                            Log.Warning("PLC上包失败. PackageId={PackageId}", packageId);
                            package.SetStatus(PackageStatus.Error, $"上包失败 (序号: {package.Index})");
                            _ = _audioService.PlayPresetAsync(AudioType.SystemError);
                        }

                        Log.Debug("准备停止等待PLC确认倒计时");
                        StopWaitingCountdown();

                        _ = DispatchInvokeAsyncFireAndForget(() =>
                        {
                            var statusItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "状态");
                            if (statusItem != null)
                            {
                                statusItem.Value = package.StatusDisplay;
                                statusItem.Description = wasSuccess ? "上包完成" : isTimeout ? "上包超时" : "上包失败";
                                statusItem.StatusColor =
                                    wasSuccess ? "#4CAF50" : "#F44336"; // Green for success, Red for failure/timeout
                            }
                        });
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        Log.Warning("等待PLC上包最终结果时操作被取消");
                        package.SetStatus(PackageStatus.Error, "操作取消 (等待上包结果)");
                        Log.Debug("操作取消，准备停止等待PLC确认倒计时");
                        StopWaitingCountdown();
                    }
                    catch (Exception finalResultEx)
                    {
                        Log.Error(finalResultEx, "等待PLC上包最终结果时出错");
                        package.SetStatus(PackageStatus.Error, $"等待上包结果错误: {finalResultEx.Message}");
                        _ = _audioService.PlayPresetAsync(AudioType.SystemError);
                        Log.Debug("发生异常，准备停止等待PLC确认倒计时");
                        StopWaitingCountdown();
                    }

                    Log.Information("包裹处理完成，准备释放处理权限");
                }
                catch (OperationCanceledException cancelEx) // Catch cancellation during await
                {
                    Log.Warning(cancelEx, "{Context} PLC通信或后续处理被取消", packageContext);
                    StopUploadCountdown();
                    package.SetStatus(PackageStatus.Error, "操作取消 (PLC通信)");
                    // 确保音频服务调用不会抛出异常
                    try { _ = _audioService.PlayPresetAsync(AudioType.SystemError); }
                    catch (Exception audioEx) { Log.Error(audioEx, "{Context} 播放错误音频时发生异常", packageContext); }
                    // *** 确保释放处理权限 ***
                    try { FinalizeProcessing(package, packageContext); }
                    catch (Exception finalizeEx) { Log.Error(finalizeEx, "{Context} 释放处理权限时出错", packageContext); }
                }
                catch (TimeoutException timeoutEx) // 更具体的超时异常处理
                {
                    Log.Error(timeoutEx, "{Context} PLC通信超时", packageContext);
                    if (package.Status == PackageStatus.Created)
                        package.SetStatus(PackageStatus.Error, $"PLC通信超时: {timeoutEx.Message}");
                    StopUploadCountdown();
                    try { _ = _audioService.PlayPresetAsync(AudioType.SystemError); }
                    catch (Exception audioEx) { Log.Error(audioEx, "{Context} 播放错误音频时发生异常", packageContext); }
                    // *** 确保释放处理权限 ***
                    try { FinalizeProcessing(package, packageContext); }
                    catch (Exception finalizeEx) { Log.Error(finalizeEx, "{Context} 释放处理权限时出错", packageContext); }
                }
                catch (System.Net.Sockets.SocketException socketEx) // 网络连接异常
                {
                    Log.Error(socketEx, "{Context} PLC网络连接异常", packageContext);
                    if (package.Status == PackageStatus.Created)
                        package.SetStatus(PackageStatus.Error, $"PLC网络错误: {socketEx.Message}");
                    StopUploadCountdown();
                    try { _ = _audioService.PlayPresetAsync(AudioType.SystemError); }
                    catch (Exception audioEx) { Log.Error(audioEx, "{Context} 播放错误音频时发生异常", packageContext); }
                    // *** 确保释放处理权限 ***
                    try { FinalizeProcessing(package, packageContext); }
                    catch (Exception finalizeEx) { Log.Error(finalizeEx, "{Context} 释放处理权限时出错", packageContext); }
                }
                catch (Exception ex) // Catch other exceptions in PLC communication block
                {
                    Log.Error(ex, "{Context} 处理PLC通信时发生未预料错误", packageContext);
                    if (package.Status == PackageStatus.Created)
                        package.SetStatus(PackageStatus.Error, $"未知PLC通信错误: {ex.Message}");
                    StopUploadCountdown();
                    // 确保音频服务调用不会抛出异常
                    try { _ = _audioService.PlayPresetAsync(AudioType.SystemError); }
                    catch (Exception audioEx) { Log.Error(audioEx, "{Context} 播放错误音频时发生异常", packageContext); }
                    // *** 确保释放处理权限 ***
                    try { FinalizeProcessing(package, packageContext); }
                    catch (Exception finalizeEx) { Log.Error(finalizeEx, "{Context} 释放处理权限时出错", packageContext); }
                }
            } // End of outer try block
            finally
            {
                // This finally block is now guaranteed to execute
                Log.Debug("{Context} === Entering ProcessSinglePackageAsync finally block ===", packageContext);

                // *** 增强: 确保所有资源都被正确释放，即使在异常情况下 ***
                try
                {
                    // Unified update of history and statistics, ensuring each package is updated only once
                    UpdatePackageHistory(package);
                    UpdateStatistics(package);

                    // 确保UI更新在异常情况下也能执行
                    _ = DispatchInvokeAsyncFireAndForget(() =>
                    {
                        try { UpdateUiFromResult(package); }
                        catch (Exception uiEx)
                        {
                            Log.Error(uiEx, "{Context} 更新UI结果时发生异常", packageContext);
                        }
                    });

                    // 释放图像资源
                    try
                    {
                        package.ReleaseImage();
                        Log.Debug("{Context} 图像资源已释放", packageContext);
                    }
                    catch (Exception releaseEx)
                    {
                        Log.Error(releaseEx, "{Context} 释放图像资源时发生异常", packageContext);
                    }

                    // *** 关键: 释放处理权限，确保下一个包裹能被处理 ***
                    try
                    {
                        FinalizeProcessing(package, packageContext);
                        Log.Information("{Context} 核心处理流程结束，处理权限已释放", packageContext);
                    }
                    catch (Exception finalizeEx)
                    {
                        Log.Error(finalizeEx, "{Context} 释放处理权限时发生异常 - 严重错误，可能导致系统卡住", packageContext);
                        // 在finalize失败的情况下，尝试强制释放
                        try
                        {
                            ForceReleaseProcessingLock(packageContext);
                        }
                        catch (Exception forceEx)
                        {
                            Log.Error(forceEx, "{Context} 强制释放处理权限失败 - 系统可能已卡住", packageContext);
                        }
                    }
                }
                catch (Exception finallyEx)
                {
                    Log.Error(finallyEx, "{Context} finally块中发生未预料异常", packageContext);
                    // 最后的努力：确保处理权限被释放
                    try { ForceReleaseProcessingLock(packageContext); }
                    catch { /* 忽略最后的异常 */ }
                }
            }
        } // End of LogContext push
    }

    // *** 新增: 处理图像保存和上传到WCS的方法 ***
    // 支持可选的外部克隆图像参数，以避免被外部释放
    private async Task HandleImageSavingAndUpload(PackageInfo package, string packageContext, int packageId, BitmapSource? externalClonedImage = null)
    {
        // 优先使用外部传入的克隆图像（由调用方在外部克隆并 Freeze），否则尝试从 package.Image 克隆
        Log.Debug("{Context} 开始处理图像保存和上传到WCS (externalClonedImage set: {HasExternal}).", packageContext, externalClonedImage != null);
        BitmapSource? imageToSave = externalClonedImage;
        if (imageToSave == null)
        {
            if (package.Image == null)
            {
                Log.Warning("{Context} 包裹信息中无图像可保存或上传 (package.Image == null).", packageContext);
                var currentDisplay = package.StatusDisplay;
                package.SetStatus(package.Status, $"{currentDisplay} [无图像]");
                return;
            }

            try
            {
                imageToSave = package.Image.Clone();
                if (imageToSave.CanFreeze) imageToSave.Freeze();
                else Log.Warning("{Context} 克隆的图像无法冻结，仍尝试使用.", packageContext);
            }
            catch (Exception cloneEx)
            {
                Log.Error(cloneEx, "{Context} 克隆或冻结图像时出错.", packageContext);
                imageToSave = null;
            }

            if (imageToSave == null)
            {
                var currentDisplay = package.StatusDisplay;
                package.SetStatus(package.Status, $"{currentDisplay} [图像克隆失败]");
                return;
            }
        }

        // 保存图像到指定路径
        string? imagePath = null;
        try
        {
            imagePath = await _imageStorageService.SaveImageWithWatermarkAsync(
                imageToSave,
                package.Barcode,
                package.Weight,
                package.Length,
                package.Width,
                package.Height,
                package.CreateTime);
        }
        catch (Exception saveEx)
        {
            Log.Error(saveEx, "{Context} 异步保存图像时发生异常.", packageContext);
        }

        if (imagePath != null)
        {
            package.ImagePath = imagePath;
            Log.Information("{Context} 图像保存成功: Path={ImagePath}", packageContext, imagePath);

            // 上传图片路径到WCS
            await UploadImagePathToWcs(package, packageContext, packageId, imagePath);
        }
        else
        {
            Log.Error("{Context} 包裹图像保存失败.", packageContext);
            var currentDisplay = package.StatusDisplay;
            package.SetStatus(package.Status, $"{currentDisplay} [图像保存失败]");
        }
    }

    // *** 新增: 上传图片路径到WCS的方法 ***
    private async Task UploadImagePathToWcs(PackageInfo package, string packageContext, int packageId, string imagePath)
    {
        try
        {
            Log.Debug("{Context} 开始上传图片路径到WCS: ImagePath={ImagePath}",
                packageContext, imagePath);

            // 准备条码列表
            var barcodeList = new List<string>();
            if (!string.IsNullOrEmpty(package.Barcode)) barcodeList.Add(package.Barcode);

            // 准备二维码列表（如果有的话）
            var matrixBarcodeList = new List<string>();

            // 准备图片绝对路径列表，JdWcsCommunicationService会自动处理路径转换
            var absoluteImageUrls = new List<string> { imagePath };

            // 生成时间戳
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // 调用WCS服务上传图片路径，JdWcsCommunicationService会自动处理路径转换和URL生成
            var uploadSuccess = await _jdWcsCommunicationService.UploadImageUrlsAsync(
                packageId,
                barcodeList,
                matrixBarcodeList,
                absoluteImageUrls,
                timestamp);

            if (uploadSuccess)
            {
                Log.Information("{Context} 图片路径上传到WCS成功: TaskNo={PackageId}, ImagePath={ImagePath}",
                    packageContext, packageId, imagePath);
            }
            else
            {
                Log.Warning("{Context} 图片路径上传到WCS失败: TaskNo={PackageId}, ImagePath={ImagePath}",
                    packageContext, packageId, imagePath);
                var currentDisplay = package.StatusDisplay;
                package.SetStatus(package.Status, $"{currentDisplay} [WCS上传失败]");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Context} 上传图片路径到WCS时发生异常: TaskNo={PackageId}, ImagePath={ImagePath}",
                packageContext, packageId, imagePath);
            var currentDisplay = package.StatusDisplay;
            package.SetStatus(package.Status, $"{currentDisplay} [WCS上传异常]");
        }
    }


    // *** 添加: 处理PLC拒绝警告UI的方法 ***
    // *** Add processedPackageContext parameter ***

    private void FinalizeProcessing(PackageInfo? processedPackage, string processedPackageContext)
    {
        PackageInfo? nextPackageToProcess; // Initialize here
        // *** Remove LogContext push ***
        // *** Use passed context for logging ***
        Log.Information("{Context} [调度] Finalize: 完成处理", processedPackageContext);

        lock (_processingLock)
        {
            if (_currentlyProcessingPackage != null &&
                !ReferenceEquals(_currentlyProcessingPackage, processedPackage))
            {
                // Log the mismatch using the context of the package that *was* being processed
                var currentProcessingContext = _currentlyProcessingPackage.Index >= 0
                    ? $"[包裹{_currentlyProcessingPackage.Index}|{_currentlyProcessingPackage.Barcode}]"
                    : "[未知处理中包裹]";
                Log.Error("[调度][!!!] Finalize 时发现当前处理的包裹 ({CurrentContext}) 与完成的包裹 ({ProcessedContext}) 不匹配!",
                    currentProcessingContext, processedPackageContext);
            }

            _currentlyProcessingPackage = null; // 标记当前为空闲

            // 检查堆栈
            if (_packageStack.TryPop(out nextPackageToProcess))
            {
                // 从堆栈获取下一个
                var nextPackageContext = $"[包裹{nextPackageToProcess.Index}|{nextPackageToProcess.Barcode}]";
                Log.Information("{ProcessedContext} [调度] 从堆栈获取下一个包裹处理: {NextContext}", processedPackageContext,
                    nextPackageContext);

                _currentlyProcessingPackage = nextPackageToProcess; // 设置为当前处理
                IsNextPackageWaiting = !_packageStack.IsEmpty; // 更新等待状态

                // 清理堆栈中可能剩余的其他包裹（除了刚弹出的那个）
                // *** Pass context string and the object to exclude ***
                ClearPackageStack($"处理新包裹 {nextPackageContext} 前清理剩余堆栈",
                    processedPackageContext + " (清理堆栈)", // Context for the cleanup action
                    nextPackageToProcess); // Exclude the next package
            }
            else // 堆栈为空
            {
                _currentlyProcessingPackage = null;
                IsNextPackageWaiting = false;
                Log.Information("{Context} [调度] 堆栈为空，系统转为空闲状态", processedPackageContext);
                // 设置背景为绿色 (允许上包 / 空闲)
                _ = DispatchInvokeAsyncFireAndForget(() =>
                {
                    if (MainWindowBackgroundBrush == BackgroundSuccess) return;
                    MainWindowBackgroundBrush = BackgroundSuccess;
                    Log.Information("[状态][UI] 设置背景为 绿色 (允许上包) - 系统空闲 ({Context})", processedPackageContext);
                });
            }
        } // 结束 lock

        // 在锁外启动下一个处理（如果需要）
        if (nextPackageToProcess == null) return;
        var finalNextPackageContext = $"[包裹{nextPackageToProcess.Index}|{nextPackageToProcess.Barcode}]";
        Log.Debug("{ProcessedContext} [调度] 准备在UI线程外异步启动下一个包裹处理: {NextContext}", processedPackageContext,
            finalNextPackageContext);

        // 使用 Task.Run 确保它不在锁内或 UI 线程上阻塞启动
        // *** Pass the new context ***
        _ = Task.Run(() =>
            ProcessSinglePackageAsync(nextPackageToProcess, finalNextPackageContext, _viewModelCts.Token));
    }


    /// <summary>
    ///     合并UI更新的辅助方法 - 更新最终状态、历史记录、统计信息
    /// </summary>
    private void UpdateUiFromResult(PackageInfo package)
    {
        // 此方法应该在UI线程上运行
        // MainWindowBackgroundBrush现在由处理流程控制，而不是结果

        // 更新最后一个包裹状态标志
        LastPackageWasSuccessful = string.IsNullOrEmpty(package.ErrorMessage);

        // 更新包裹信息显示 (状态, 描述, 颜色)
        UpdatePackageInfoItemsStatusFinal(package); // 更新最终状态文本/颜色

        // *** 移除历史记录和统计信息更新，因为已在PLC接受时更新 ***
        // UpdatePackageHistory(package); // 已在PLC接受时更新
        // UpdateStatistics(package); // 已在PLC接受时更新

        // 可选, 根据结果添加一个短暂的视觉闪烁? (例如, 错误时闪烁红色)
        // 目前, 我们坚持使用黄色/绿色的加载状态背景.
    }

    /// <summary>
    ///     更新包裹信息项的最终状态部分 (状态文本, 颜色, 描述)
    /// </summary>
    private void UpdatePackageInfoItemsStatusFinal(PackageInfo package)
    {
        var statusItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "状态");
        if (statusItem == null) return;

        // 提取PackageId用于显示
        var packageId = GetPackageIdFromInformation(package.StatusDisplay);

        if (string.IsNullOrEmpty(package.ErrorMessage)) // 成功
        {
            // 在状态值中包含PackageId信息
            statusItem.Value = packageId != "N/A"
                ? $"{package.StatusDisplay} (ID: {packageId})"
                : package.StatusDisplay;
            statusItem.Description = packageId != "N/A"
                ? $"PLC 包裹流水号: {packageId}"
                : "上包处理成功";
            statusItem.StatusColor = "#4CAF50"; // 绿色
        }
        else // Error
        {
            // 在错误状态中也尝试显示PackageId（如果有的话）
            statusItem.Value = packageId != "N/A"
                ? $"{package.ErrorMessage} (ID: {packageId})"
                : package.ErrorMessage;

            if (package.ErrorMessage.StartsWith("上包超时"))
            {
                statusItem.Description = packageId != "N/A"
                    ? $"上包请求未收到 PLC 响应 (流水号: {packageId})"
                    : "上包请求未收到 PLC 响应";
                statusItem.StatusColor = "#FFC107"; // 黄色 (超时)
            }
            else if (package.ErrorMessage.StartsWith("上包拒绝"))
            {
                statusItem.Description = packageId != "N/A"
                    ? $"PLC 拒绝了上包请求 (流水号: {packageId})"
                    : "PLC 拒绝了上包请求";
                statusItem.StatusColor = "#F44336"; // 红色 (拒绝)
            }
            else if (package.ErrorMessage.StartsWith("PLC状态异常"))
            {
                statusItem.Description = "PLC设备当前状态不允许上包";
                statusItem.StatusColor = "#F44336"; // 红色 (PLC异常状态)
            }
            else // 通用错误
            {
                statusItem.Description = packageId != "N/A"
                    ? $"处理失败 (序号: {package.Index}, 流水号: {packageId})"
                    : $"处理失败 (序号: {package.Index})";
                statusItem.StatusColor = "#F44336"; // 红色 (通用错误)
            }
        }
    }

    /// <summary>
    ///     更新非状态项的新辅助方法，可在异步操作之前调用
    /// </summary>
    private void UpdatePackageInfoItemsBasic(PackageInfo package)
    {
        // Ensure runs on UI thread
        _ = DispatchInvokeAsyncFireAndForget(() =>
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
                sizeItem.Unit = "cm"; // 确保单位正确设置为cm
            }

            var timeItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "时间");
            if (timeItem == null) return;
            timeItem.Value = package.CreateTime.ToString("HH:mm:ss");
            timeItem.Description = $"处理于 {package.CreateTime:yyyy-MM-dd}";
        });
    }


    /// <summary>
    ///     更新包裹历史记录
    /// </summary>
    private void UpdatePackageHistory(PackageInfo package)
    {
        // Ensure runs on UI thread
        _ = DispatchInvokeAsyncFireAndForget(() =>
        {
            try
            {
                if (_disposed)
                {
                    Log.Debug("ViewModel已释放，跳过更新包裹历史记录");
                    return;
                }

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
                while (PackageHistory.Count > maxHistoryCount) PackageHistory.RemoveAt(PackageHistory.Count - 1);
            }
            catch (Exception ex)
            {
                // Use context in error log
                var context = $"[包裹{package.Index}|{package.Barcode}]";
                Log.Error(ex, "{Context} 更新历史包裹列表时发生错误", context);
            }
        });
    }

    /// <summary>
    ///     从状态显示文本中提取PackageId
    /// </summary>
    [GeneratedRegex(@"包裹流水号:\s*(\d+)")]
    private static partial Regex PackageIdRegex();

    /// <summary>
    ///     提取PackageId的辅助方法
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
        _ = DispatchInvokeAsyncFireAndForget(() =>
        {
            try
            {
                // 更新成功/失败数
                var isSuccess = string.IsNullOrEmpty(package.ErrorMessage);
                var targetLabel = isSuccess ? "成功数" : "失败数";
                var statusItem = StatisticsItems.FirstOrDefault(x => x.Label == targetLabel);
                if (statusItem != null)
                    statusItem.Value =
                        int.TryParse(statusItem.Value, out var count)
                            ? (count + 1).ToString()
                            : "1"; // 如果解析失败则重置

                // 更新处理速率（每小时包裹数）
                var speedItem = StatisticsItems.FirstOrDefault(static x => x.Label == "处理速率");
                if (speedItem == null || PackageHistory.Count < 2) return;

                // 使用已分配的处理时间, 如果可用, 否则回退到CreateTime
                var latestTime = package.CreateTime; // 使用实际包裹时间(processedPackage不在范围内)
                // 在历史中找到最早的时间(考虑限制历史大小以提高性能)
                var earliestTime = PackageHistory.Count > 0 ? PackageHistory[^1].CreateTime : latestTime;


                var timeSpan = latestTime - earliestTime;

                if (timeSpan.TotalSeconds > 1)
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

    // 新增：仅在进入处理逻辑时递增"总包裹数"
    private void IncrementTotalPackages()
    {
        _ = DispatchInvokeAsyncFireAndForget(() =>
        {
            try
            {
                if (_disposed)
                {
                    Log.Debug("ViewModel已释放，跳过递增总包裹数");
                    return;
                }

                var totalItem = StatisticsItems.FirstOrDefault(static x => x.Label == "总包裹数");
                if (totalItem == null) return;
                totalItem.Value = int.TryParse(totalItem.Value, out var total)
                    ? (total + 1).ToString()
                    : "1"; // 如果解析失败则重置
            }
            catch (Exception ex)
            {
                Log.Error(ex, "递增总包裹数时发生错误");
            }
        });
    }

    /// <summary>
    ///     检查条码是否为母条码（以 "-2-2-" 或 "-1-2-" 或 "-1-1-" 结尾）
    /// </summary>
    private static bool IsParentBarcode(string? barcode)
    {
        return barcode != null && (barcode.EndsWith("-2-2-") || barcode.EndsWith("-1-2-") || barcode.EndsWith("-1-1-"));
    }

    /// <summary>
    ///     获取条码的前缀（移除母条码后缀）
    /// </summary>
    private static string GetBarcodePrefix(string? barcode)
    {
        if (barcode == null) return string.Empty;

        if (barcode.EndsWith("-2-2-")) return barcode[..^5]; // 移除 "-2-2-"

        if (barcode.EndsWith("-1-2-")) return barcode[..^5]; // 移除 "-1-2-"

        if (barcode.EndsWith("-1-1-")) return barcode[..^5]; // 移除 "-1-1-"

        return barcode;
    }

    /// <summary>
    ///     合并两个相关的 PackageInfo 对象
    /// </summary>
    private static PackageInfo MergePackageInfo(PackageInfo p1, PackageInfo p2)
    {
        // 确定哪个是基础包（无后缀），哪个是后缀包
        var basePackage = IsParentBarcode(p1.Barcode) ? p2 : p1;
        var suffixPackage = IsParentBarcode(p1.Barcode) ? p1 : p2;

        // 创建新的 PackageInfo - 不要在这里重用 p1/p2 的 Index。稍后分配索引。
        var mergedPackage = PackageInfo.Create();

        // 使用较早的 CreateTime
        mergedPackage.SetTriggerTimestamp(basePackage.CreateTime < suffixPackage.CreateTime
            ? basePackage.CreateTime
            : suffixPackage.CreateTime);

        // 合并条码: prefix,suffix-barcode，并添加母条码后缀 -1-1-
        var prefix = GetBarcodePrefix(basePackage.Barcode);
        // 确保 suffixPackage 条码不为空/空
        var combinedBarcode =
            string.IsNullOrEmpty(suffixPackage.Barcode) ? prefix : $"{prefix},{suffixPackage.Barcode}";
        // 添加母条码后缀 -1-1-
        combinedBarcode = $"{combinedBarcode}-1-1-";
        mergedPackage.SetBarcode(combinedBarcode);


        // 优先使用 basePackage 的数据，如果缺失则用 suffixPackage 的
        mergedPackage.SetSegmentCode(basePackage.SegmentCode);
        mergedPackage.SetWeight(basePackage.Weight > 0 ? basePackage.Weight : suffixPackage.Weight);

        // 优先使用 basePackage 的尺寸
        if (basePackage is { Length: > 0, Width: > 0, Height: > 0 })
            mergedPackage.SetDimensions(basePackage.Length.Value, basePackage.Width.Value, basePackage.Height.Value);
        else if (suffixPackage is { Length: > 0, Width: > 0, Height: > 0 })
            mergedPackage.SetDimensions(suffixPackage.Length.Value, suffixPackage.Width.Value,
                suffixPackage.Height.Value);

        // 图像处理：优先使用 basePackage 的图像，并克隆/冻结
        BitmapSource? imageToUse = null;
        string? imagePathToUse = null; // 保留图像路径（如果可用）

        var sourceImage = basePackage.Image ?? suffixPackage.Image;
        var sourceImagePath = basePackage.ImagePath ?? suffixPackage.ImagePath; // 获取可能的路径

        if (sourceImage != null)
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

        mergedPackage.SetImage(imageToUse, imagePathToUse); // 设置克隆/冻结的图像和路径

        // 初始状态是 Created
        Log.Debug(
            "合并后的包裹信息: Barcode='{Barcode}', Weight={Weight}, Dimensions='{Dims}', ImageSet={HasImage}, ImagePath='{Path}'",
            mergedPackage.Barcode, mergedPackage.Weight, mergedPackage.VolumeDisplay, mergedPackage.Image != null,
            mergedPackage.ImagePath ?? "N/A");


        return mergedPackage;
    }

    /// <summary>
    ///     从配置中加载条码模式设置
    /// </summary>
    private void LoadBarcodeModeFromSettings()
    {
        try
        {
            if (_disposed)
            {
                Log.Debug("ViewModel已释放，跳过加载条码模式配置");
                return;
            }

            var config = _settings_service.LoadSettings<HostConfiguration>();
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
    ///     保存条码模式到配置
    /// </summary>
    private void SaveBarcodeModeToSettings(BarcodeMode mode)
    {
        try
        {
            if (_disposed)
            {
                Log.Debug("ViewModel已释放，跳过保存条码模式配置");
                return;
            }

            var config = _settings_service.LoadSettings<HostConfiguration>();
            if (config.BarcodeMode == mode) return; // 不需要更改

            config.BarcodeMode = mode;
            _settings_service.SaveSettings(config);
            Log.Information("条码模式已保存到配置: {Mode}", GetBarcodeModeDisplayText(mode));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存条码模式配置时出错");
        }
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;

        Log.Information("[Dispose] 开始释放 MainWindowViewModel (disposing={IsDisposing})...", disposing);

        try
        {
            if (disposing)
            {
                try
                {
                    _viewModelCts.Cancel(); // 取消所有操作
                    Log.Debug("[Dispose] CancellationToken已取消.");
                }
                catch (Exception ctsEx)
                {
                    Log.Error(ctsEx, "[Dispose] 取消CancellationToken时发生错误");
                }

                // 停止图像处理队列
                try
                {
                    _imageProcessingCts.Cancel();
                    _imageProcessingChannel.Writer.Complete();

                    // 等待图像处理任务完成，最多等待5秒
                    try
                    {
                        var waitTask = _imageProcessingTask.WaitAsync(TimeSpan.FromSeconds(5));
                        waitTask.Wait(); // 等待任务完成

                        if (waitTask.IsCompletedSuccessfully)
                        {
                            Log.Debug("[Dispose] 图像处理队列已正常停止.");
                        }
                        else
                        {
                            Log.Warning("[Dispose] 图像处理队列在5秒内未能完成，已强制终止.");
                        }
                    }
                    catch (AggregateException)
                    {
                        Log.Warning("[Dispose] 等待图像处理队列完成时发生异常，已强制终止.");
                    }
                }
                catch (Exception imageQueueEx)
                {
                    Log.Error(imageQueueEx, "[Dispose] 停止图像处理队列时发生错误");
                }

                // 取消订阅 - 每个操作都单独try-catch
                try
                {
                    _cameraService.ConnectionChanged -= OnCameraConnectionChanged;
                }
                catch (Exception cameraEx)
                {
                    Log.Error(cameraEx, "[Dispose] 取消相机连接事件订阅时发生错误");
                }

                try
                {
                    _plcCommunicationService.DeviceStatusChanged -= OnPlcDeviceStatusChanged;
                }
                catch (Exception plcEx)
                {
                    Log.Error(plcEx, "[Dispose] 取消PLC状态变更事件订阅时发生错误");
                }

                try
                {
                    _jdWcsCommunicationService.ConnectionChanged -= OnJdWcsConnectionChanged;
                }
                catch (Exception jdWcsEx)
                {
                    Log.Error(jdWcsEx, "[Dispose] 取消JD WCS连接事件订阅时发生错误");
                }

                try
                {
                    foreach (var sub in _subscriptions) sub.Dispose();
                    _subscriptions.Clear();
                    Log.Debug("[Dispose] 所有事件订阅已取消并清理.");
                }
                catch (Exception subEx)
                {
                    Log.Error(subEx, "[Dispose] 清理订阅时发生错误");
                }

                // 停止定时器 - 每个定时器都单独try-catch
                try
                {
                    _timer.Stop();
                }
                catch (Exception timerEx)
                {
                    Log.Error(timerEx, "[Dispose] 停止主定时器时发生错误");
                }

                try
                {
                    _uploadCountdownTimer?.Stop();
                    if (_uploadCountdownTimer != null) _uploadCountdownTimer.Tick -= UploadCountdownTimer_Tick;
                }
                catch (Exception uploadTimerEx)
                {
                    Log.Error(uploadTimerEx, "[Dispose] 停止上包倒计时定时器时发生错误");
                }

                // 安全停止等待倒计时定时器（使用同步块）
                try
                {
                    lock (_countdownLock)
                    {
                        _waitingCountdownTimer?.Stop();
                        if (_waitingCountdownTimer != null) _waitingCountdownTimer.Tick -= WaitingCountdownTimer_Tick;
                    }
                }
                catch (Exception waitingTimerEx)
                {
                    Log.Error(waitingTimerEx, "[Dispose] 停止等待倒计时定时器时发生错误");
                }

                try
                {
                    var uploadTimerStopped = _uploadCountdownTimer?.IsEnabled == false;
                    var waitingTimerStopped = IsWaitingCountdownTimerRunning();
                    Log.Debug("[Dispose] 定时器已停止 - 上包倒计时: {UploadTimerStopped}, 等待倒计时: {WaitingTimerStopped}",
                        uploadTimerStopped, !waitingTimerStopped);
                }
                catch (Exception statusEx)
                {
                    Log.Error(statusEx, "[Dispose] 检查定时器状态时发生错误");
                }

                // 清理包裹状态
                try
                {
                    // Pass a string context for the reason
                    ClearPackageStack("[Dispose] 清理包裹堆栈", "[DisposeCtx]");
                }
                catch (Exception stackEx)
                {
                    Log.Error(stackEx, "[Dispose] 清理包裹堆栈时发生错误");
                }

                try
                {
                    lock (_processingLock)
                    {
                        _currentlyProcessingPackage?.ReleaseImage();
                        _currentlyProcessingPackage = null;
                    }
                }
                catch (Exception processingEx)
                {
                    Log.Error(processingEx, "[Dispose] 清理当前处理包裹时发生错误");
                }

                try
                {
                    Log.Debug("[Dispose] 当前处理和堆栈中的包裹已清理.");
                }
                catch (Exception)
                {
                    // 日志失败时不抛出异常
                }

                try
                {
                    _viewModelCts.Dispose(); // 处置 CancellationTokenSource
                }
                catch (Exception ctsDisposeEx)
                {
                    Log.Error(ctsDisposeEx, "[Dispose] 处置CancellationTokenSource时发生错误");
                }

                try
                {
                    _imageProcessingCts.Dispose(); // 处置图像处理CancellationTokenSource
                }
                catch (Exception imageCtsDisposeEx)
                {
                    Log.Error(imageCtsDisposeEx, "[Dispose] 处置图像处理CancellationTokenSource时发生错误");
                }
            }

            Log.Information("[Dispose] MainWindowViewModel 处置完毕.");
        }
        catch (Exception overallEx)
        {
            Log.Error(overallEx, "[Dispose] 释放资源时发生总体错误");
        }
        finally
        {
            _disposed = true;
        }
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
            Log.Debug("[State] 清理了过期的超时前缀: {Prefix}", key);
    }



    // *** 移除: 不再处理PLC上包最终结果事件 ***
    // private void OnPlcUploadResultReceived(object? sender, (ushort CommandId, bool IsTimeout, int PackageId) result)
    // {
    //     var (commandId, isTimeout, packageId) = result;
    //
    //     // 从缓存中获取对应的包裹
    //     if (!_pendingFinalResultPackages.TryRemove(commandId, out var package))
    //     {
    //         Log.Warning("收到 CommandId={CommandId} 的最终结果，但未找到对应的缓存包裹", commandId);
    //         return;
    //     }
    //
    //     // *** 新增: 清理对应的超时管理器 ***
    //     if (_pendingFinalResultTimeouts.TryRemove(commandId, out var timeoutCts))
    //     {
    //         timeoutCts.Cancel(); // 取消超时
    //         timeoutCts.Dispose();
    //         Log.Debug("已清理 CommandId={CommandId} 的超时管理器", commandId);
    //     }
    //
    //     var packageContext = $"[包裹{package.Index}|{package.Barcode}]";
    //     Log.Information("{Context} 收到PLC最终结果: CommandId={CommandId}, IsTimeout={IsTimeout}, PackageId={PackageId}",
    //         packageContext, commandId, isTimeout, packageId);
    //
    //     // 异步处理最终结果，避免阻塞PLC通信线程
    //     _ = Task.Run(async () =>
    //     {
    //         try
    //         {
    //             await ProcessFinalUploadResult(package, packageContext, commandId, isTimeout, packageId, _viewModelCts.Token);
    //         }
    //         catch (Exception ex)
    //         {
    //             Log.Error(ex, "{Context} 处理最终上包结果时发生错误", packageContext);
    //             // 确保包裹资源被释放
    //             package.ReleaseImage();
    //         }
    //     });
    // }

    // *** 移除: 不再处理最终上包结果的方法 ***
    // private async Task ProcessFinalUploadResult(PackageInfo package, string packageContext, ushort commandId, bool isTimeout, int packageId, CancellationToken cancellationToken)
    // {
    //     try
    //     {
    //         if (isTimeout)
    //         {
    //             Log.Warning("{Context} PLC最终结果超时. CommandId={CommandId}", packageContext, commandId);
    //             package.SetStatus(PackageStatus.LoadingTimeout, $"上包结果超时 (序号: {package.Index})");
    //             _ = _audioService.PlayPresetAsync(AudioType.LoadingTimeout);
    //         }
    //         else if (packageId <= 0)
    //         {
    //             Log.Error("{Context} PLC报告上包处理失败. CommandId={CommandId}, PackageId={PackageId}", packageContext, commandId, packageId);
    //             package.SetStatus(PackageStatus.Error, $"PLC处理失败 (序号: {package.Index})");
    //             _ = _audioService.PlayPresetAsync(AudioType.SystemError);
    //         }
    //         else
    //         {
    //             // PLC 成功
    //             package.SetStatus(PackageStatus.LoadingSuccess, $"上包完成 (PLC流水号: {packageId})");
    //             Log.Information("{Context} PLC报告上包成功. CommandId={CommandId}, PLC流水号={PackageId}", packageContext, commandId, packageId);
    //             _ = _audioService.PlayPresetAsync(AudioType.LoadingSuccess);
    //
    //             // 处理图像保存
    //             await HandleImageSaving(package, packageContext, packageId, cancellationToken);
    //         }
    //
    //         // 更新UI (不再重复更新PackageHistory和Statistics，因为已在PLC接受时更新)
    //         _ = DispatchInvokeAsyncFireAndForget(() =>
    //         {
    //             UpdateUiFromResult(package);
    //             // *** 移除重复的PackageHistory和Statistics更新，因为已在PLC接受时更新 ***
    //             // UpdatePackageHistory(package); // 已在PLC接受时更新
    //             // UpdateStatistics(package); // 已在PLC接受时更新
    //         });
    //     }
    //     catch (Exception ex)
    //     {
    //         Log.Error(ex, "{Context} 处理最终上包结果时发生内部错误", packageContext);
    //         package.SetStatus(PackageStatus.Error, $"处理最终结果时出错: {ex.Message}");
    //         _ = DispatchInvokeAsyncFireAndForget(() => UpdateUiFromResult(package));
    //     }
    //     finally
    //     {
    //         // 释放图像资源
    //         package.ReleaseImage();
    //         Log.Debug("{Context} 最终结果处理完成，图像资源已释放", packageContext);
    //     }
    // }

    // 处理PLC设备状态变更事件 - 统一处理所有状态变更
    private void OnPlcDeviceStatusChanged(object? sender, DeviceStatusCode statusCode)
    {
        try
        {
            // 获取对应的状态信息
            var statusText = GetDeviceStatusDisplayText(statusCode);
            var description = GetDeviceStatusDescription(statusCode);
            var color = GetDeviceStatusColor(statusCode);

            // 更新PLC独立状态属性
            _ = DispatchInvokeAsyncFireAndForget(() =>
            {
                try
                {
                    if (_disposed)
                    {
                        Log.Debug("[PLC] ViewModel已释放，跳过PLC状态更新");
                        return;
                    }

                    PlcStatusText = statusText;
                    PlcStatusDescription = description;
                    PlcStatusColor = color;
                }
                catch (Exception uiEx)
                {
                    Log.Error(uiEx, "[PLC] 更新PLC独立状态属性时发生错误");
                }
            });

            // 更新 DeviceStatuses 列表中的 PLC 条目
            _ = DispatchInvokeAsyncFireAndForget(() =>
            {
                try
                {
                    if (_disposed)
                    {
                        Log.Debug("[PLC] ViewModel已释放，跳过DeviceStatuses更新");
                        return;
                    }

                    var plcStatusEntry = DeviceStatuses.FirstOrDefault(s => s.Name == "PLC");
                    if (plcStatusEntry == null) return;
                    plcStatusEntry.Status = statusText; // 使用翻译的文本
                    plcStatusEntry.StatusColor = color;
                }
                catch (Exception statusEx)
                {
                    Log.Error(statusEx, "[PLC] 更新DeviceStatuses列表时发生错误");
                }
            });

            Log.Information("PLC设备状态已更新: {Status}, {Description}", statusText, description);

            // 如果PLC状态恢复正常，隐藏PLC异常警告
            if (statusCode != DeviceStatusCode.Normal || !IsPlcAbnormalWarningVisible) return;

            Log.Information("PLC状态恢复正常，隐藏PLC异常警告。");
            _ = DispatchInvokeAsyncFireAndForget(() =>
            {
                try
                {
                    if (!_disposed)
                    {
                        IsPlcAbnormalWarningVisible = false;
                    }
                }
                catch (Exception warningEx)
                {
                    Log.Error(warningEx, "[PLC] 隐藏PLC异常警告时发生错误");
                }
            });
        }
        catch (Exception overallEx)
        {
            Log.Error(overallEx, "[PLC] 处理PLC设备状态变更时发生总体错误: StatusCode={StatusCode}", statusCode);
        }
    }

    // 处理JD WCS连接状态变更事件
    private void OnJdWcsConnectionChanged(object? sender, bool isConnected)
    {
        try
        {
            Log.Information("JD WCS连接状态变更: {Status}", isConnected ? "已连接" : "已断开");

            var statusText = isConnected ? "已连接" : "未连接";
            var statusColor = isConnected ? "#4CAF50" : "#F44336";
            var statusDescription = isConnected ? "京东WCS服务连接正常，可以上传数据" : "京东WCS服务未连接，请检查网络连接";

            // 更新JD WCS独立状态属性
            _ = DispatchInvokeAsyncFireAndForget(() =>
            {
                try
                {
                    if (_disposed)
                    {
                        Log.Debug("[JD WCS] ViewModel已释放，跳过JD WCS状态更新");
                        return;
                    }

                    JdWcsStatusText = statusText;
                    JdWcsStatusDescription = statusDescription;
                    JdWcsStatusColor = statusColor;
                }
                catch (Exception uiEx)
                {
                    Log.Error(uiEx, "[JD WCS] 更新JD WCS独立状态属性时发生错误");
                }
            });

            // 更新 DeviceStatuses 列表中的 JD WCS 条目
            _ = DispatchInvokeAsyncFireAndForget(() =>
            {
                try
                {
                    if (_disposed)
                    {
                        Log.Debug("[JD WCS] ViewModel已释放，跳过DeviceStatuses更新");
                        return;
                    }

                    var jdWcsStatusEntry = DeviceStatuses.FirstOrDefault(s => s.Name == "JD WCS");
                    if (jdWcsStatusEntry == null) return;
                    jdWcsStatusEntry.Status = statusText;
                    jdWcsStatusEntry.StatusColor = statusColor;
                }
                catch (Exception statusEx)
                {
                    Log.Error(statusEx, "[JD WCS] 更新DeviceStatuses列表时发生错误");
                }
            });
        }
        catch (Exception overallEx)
        {
            Log.Error(overallEx, "[JD WCS] 处理JD WCS连接状态变更时发生总体错误: IsConnected={IsConnected}", isConnected);
        }
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

    /// <summary>
    ///     根据相机数量计算最佳布局
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
    ///     获取指定相机在网格中的位置
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
        try
        {
            if (Application.Current?.Dispatcher?.HasShutdownStarted == true) return;
            if (Application.Current?.MainWindow == null || !Application.Current.MainWindow.IsLoaded) return;

            var previousValue = UploadCountdownValue;
            UploadCountdownValue--;

            Log.Debug("[倒计时] UploadCountdownTimer Tick事件 - 值: {Previous} -> {Current}", previousValue,
                UploadCountdownValue);

            if (UploadCountdownValue > 0) return;
            StopUploadCountdown();
            Log.Information("[倒计时] 上包倒计时结束");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UploadCountdownTimer_Tick 发生错误");
        }
    }

    private void StopUploadCountdown()
    {
        var wasRunning = _uploadCountdownTimer?.IsEnabled ?? false;
        var wasVisible = IsUploadCountdownVisible;
        var currentValue = UploadCountdownValue;

        _uploadCountdownTimer?.Stop();
        IsUploadCountdownVisible = false;

        Log.Information("[倒计时] 停止上包倒计时 - 之前状态: TimerRunning={WasRunning}, IsVisible={WasVisible}, Value={CurrentValue}",
            wasRunning, wasVisible, currentValue);
    }

    /// <summary>
    ///     安全地检查等待倒计时定时器是否正在运行
    /// </summary>
    private bool IsWaitingCountdownTimerRunning()
    {
        lock (_countdownLock)
        {
            return _waitingCountdownTimer?.IsEnabled ?? false;
        }
    }

    // 等待PLC确认倒计时相关方法
    private void StartWaitingCountdown(int totalSeconds)
    {
        lock (_countdownLock)
        {
            try
            {
                // 添加状态检查日志
                Log.Debug(
                    "[等待倒计时] StartWaitingCountdown 被调用 - 参数: {TotalSeconds}秒, 当前状态: IsWaiting={IsWaiting}, Seconds={Seconds}, TimerRunning={TimerRunning}",
                    totalSeconds, IsWaitingForUploadResult, WaitingCountdownSeconds,
                    IsWaitingCountdownTimerRunning());

                if (totalSeconds <= 0)
                {
                    Log.Warning("等待倒计时配置无效 ({Value})，将不启动倒计时", totalSeconds);
                    StopWaitingCountdown();
                    return;
                }

                // 检查是否已经在运行
                if (IsWaitingForUploadResult && IsWaitingCountdownTimerRunning())
                {
                    Log.Warning("[等待倒计时] 等待倒计时已在运行中，跳过重复启动 - 当前值: {CurrentSeconds}", WaitingCountdownSeconds);
                    return;
                }

                // 异步启动倒计时，避免死锁
                try
                {
                    if (Application.Current?.Dispatcher != null &&
                        !Application.Current.Dispatcher.HasShutdownStarted)
                    {
                        // 总是使用异步方式，避免死锁
                        _ = Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            try
                            {
                                StartWaitingCountdownOnUIThread(totalSeconds);
                            }
                            catch (Exception uiEx)
                            {
                                Log.Error(uiEx, "[等待倒计时] 在UI线程上启动倒计时失败");
                            }
                        }, DispatcherPriority.Normal);
                    }
                    else
                    {
                        // Dispatcher不可用，直接执行（可能在测试环境中）
                        StartWaitingCountdownOnUIThread(totalSeconds);
                    }
                }
                catch (Exception dispatcherEx)
                {
                    Log.Error(dispatcherEx, "[等待倒计时] 启动倒计时失败");
                    // 尝试使用现有的fire-and-forget方法作为fallback
                    try
                    {
                        _ = DispatchInvokeAsyncFireAndForget(() =>
                        {
                            try
                            {
                                StartWaitingCountdownOnUIThread(totalSeconds);
                            }
                            catch (Exception asyncEx)
                            {
                                Log.Error(asyncEx, "[等待倒计时] 异步启动倒计时也失败");
                            }
                        });
                    }
                    catch (Exception fallbackEx)
                    {
                        Log.Error(fallbackEx, "[等待倒计时] 所有启动方式都失败");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[等待倒计时] 启动等待倒计时出现异常");
                // 在异常情况下也要重置状态，但避免递归调用StopWaitingCountdown
                try
                {
                    if (Application.Current?.Dispatcher != null &&
                        !Application.Current.Dispatcher.HasShutdownStarted)
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            IsWaitingForUploadResult = false;
                            WaitingCountdownSeconds = 0;
                            WaitingCountdownProgress = 0.0;
                        });
                    }
                    else
                    {
                        IsWaitingForUploadResult = false;
                        WaitingCountdownSeconds = 0;
                        WaitingCountdownProgress = 0.0;
                    }
                }
                catch (Exception resetEx)
                {
                    Log.Error(resetEx, "[等待倒计时] 重置状态也失败");
                }
            }
        }
    }

    /// <summary>
    ///     在UI线程上启动等待倒计时的实际操作
    /// </summary>
    private void StartWaitingCountdownOnUIThread(int totalSeconds)
    {
        lock (_countdownLock)
        {
            try
            {
                // 检查ViewModel是否已释放
                if (_disposed)
                {
                    Log.Debug("[等待倒计时] ViewModel已释放，跳过启动倒计时");
                    return;
                }

                // 检查是否已经在运行
                if (IsWaitingForUploadResult && IsWaitingCountdownTimerRunning())
                {
                    Log.Warning("[等待倒计时] 倒计时已在UI线程上运行，跳过重复启动");
                    return;
                }

                // 原子性地设置所有相关属性
                try
                {
                    _waitingCountdownTotalSeconds = totalSeconds;
                    WaitingCountdownSeconds = totalSeconds;
                    WaitingCountdownProgress = 100.0; // 开始时进度为100%
                    WaitingStatusText = "等待PLC确认上包...";
                    IsWaitingForUploadResult = true; // 最后设置这个属性，触发UI更新
                }
                catch (Exception stateEx)
                {
                    Log.Error(stateEx, "[等待倒计时] 设置倒计时状态失败");
                    return;
                }

                // 初始化定时器（如果还未初始化）
                try
                {
                    if (_waitingCountdownTimer == null)
                    {
                        _waitingCountdownTimer = new DispatcherTimer(DispatcherPriority.Normal)
                        {
                            Interval = TimeSpan.FromSeconds(1)
                        };
                        _waitingCountdownTimer.Tick += WaitingCountdownTimer_Tick;
                        Log.Debug("[等待倒计时] 创建新的等待倒计时定时器");
                    }
                }
                catch (Exception timerCreateEx)
                {
                    Log.Error(timerCreateEx, "[等待倒计时] 创建定时器失败");
                    // 重置状态
                    try
                    {
                        IsWaitingForUploadResult = false;
                        WaitingCountdownSeconds = 0;
                        WaitingCountdownProgress = 0.0;
                    }
                    catch (Exception resetEx)
                    {
                        Log.Error(resetEx, "[等待倒计时] 重置状态失败");
                    }
                    return;
                }

                // 启动定时器
                try
                {
                    _waitingCountdownTimer.Start();
                    Log.Information("[等待倒计时] 启动等待PLC确认倒计时: {Seconds} 秒", totalSeconds);
                }
                catch (Exception startEx)
                {
                    Log.Error(startEx, "[等待倒计时] 启动定时器失败");
                    // 重置状态
                    try
                    {
                        IsWaitingForUploadResult = false;
                        WaitingCountdownSeconds = 0;
                        WaitingCountdownProgress = 0.0;
                    }
                    catch (Exception resetEx)
                    {
                        Log.Error(resetEx, "[等待倒计时] 重置状态失败");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[等待倒计时] 在UI线程上启动倒计时出现总体错误");
                // 最后的努力：重置状态
                try
                {
                    IsWaitingForUploadResult = false;
                    WaitingCountdownSeconds = 0;
                    WaitingCountdownProgress = 0.0;
                }
                catch (Exception finalEx)
                {
                    Log.Error(finalEx, "[等待倒计时] 最终状态重置也失败");
                }
            }
        }
    }

    private void WaitingCountdownTimer_Tick(object? sender, EventArgs e)
    {
        lock (_countdownLock)
        {
            try
            {
                // 检查应用状态
                if (Application.Current?.Dispatcher?.HasShutdownStarted == true) return;
                if (Application.Current?.MainWindow == null || !Application.Current.MainWindow.IsLoaded) return;

                // 再次检查ViewModel是否已释放
                if (_disposed)
                {
                    Log.Debug("[等待倒计时] ViewModel已释放，停止倒计时");
                    _waitingCountdownTimer?.Stop();
                    return;
                }

                // 检查倒计时是否仍然有效
                if (!IsWaitingForUploadResult || WaitingCountdownSeconds <= 0)
                {
                    try
                    {
                        StopWaitingCountdown();
                        if (WaitingCountdownSeconds <= 0)
                        {
                            Log.Information("[等待倒计时] 等待PLC确认倒计时结束");
                        }
                    }
                    catch (Exception stopEx)
                    {
                        Log.Error(stopEx, "[等待倒计时] Tick事件中停止倒计时失败");
                    }
                    return;
                }

                var previousSeconds = WaitingCountdownSeconds;
                try
                {
                    WaitingCountdownSeconds--;
                }
                catch (Exception updateEx)
                {
                    Log.Error(updateEx, "[等待倒计时] 更新倒计时秒数失败");
                    return;
                }

                // 更新进度条（从100%递减到0%）
                try
                {
                    if (_waitingCountdownTotalSeconds > 0)
                    {
                        WaitingCountdownProgress = (double)WaitingCountdownSeconds / _waitingCountdownTotalSeconds * 100.0;
                    }
                }
                catch (Exception progressEx)
                {
                    Log.Error(progressEx, "[等待倒计时] 更新进度条失败");
                }

                try
                {
                    Log.Debug("[等待倒计时] Tick事件 - 秒数: {Previous} -> {Current}, 进度: {Progress:F1}%",
                        previousSeconds, WaitingCountdownSeconds, WaitingCountdownProgress);
                }
                catch (Exception)
                {
                    // 日志失败不影响倒计时继续
                }

                // 检查是否到达结束时间
                if (WaitingCountdownSeconds <= 0)
                {
                    Log.Information("[等待倒计时] 等待PLC确认倒计时结束");
                    try
                    {
                        StopWaitingCountdown();
                    }
                    catch (Exception stopEx)
                    {
                        Log.Error(stopEx, "[等待倒计时] Tick事件中到达结束时间时停止倒计时失败");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[等待倒计时] WaitingCountdownTimer_Tick发生错误");
                // 发生异常时停止倒计时
                try
                {
                    StopWaitingCountdown();
                }
                catch (Exception stopEx)
                {
                    Log.Error(stopEx, "[等待倒计时] 在处理Tick异常时停止倒计时也失败了");
                    // 最后的努力：直接重置状态
                    try
                    {
                        IsWaitingForUploadResult = false;
                        WaitingCountdownSeconds = 0;
                        WaitingCountdownProgress = 0.0;
                        _waitingCountdownTimer?.Stop();
                    }
                    catch (Exception finalEx)
                    {
                        Log.Error(finalEx, "[等待倒计时] 所有清理操作都失败");
                    }
                }
            }
        }
    }

    private void StopWaitingCountdown()
    {
        lock (_countdownLock)
        {
            try
            {
                var wasWaiting = IsWaitingForUploadResult;
                var wasRunning = IsWaitingCountdownTimerRunning();
                var currentSeconds = WaitingCountdownSeconds;
                var currentProgress = WaitingCountdownProgress;

                // 停止定时器
                try
                {
                    _waitingCountdownTimer?.Stop();
                }
                catch (Exception timerEx)
                {
                    Log.Error(timerEx, "[等待倒计时] 停止定时器时发生错误");
                }

                // 异步重置UI属性，避免死锁
                try
                {
                    if (Application.Current?.Dispatcher != null &&
                        !Application.Current.Dispatcher.HasShutdownStarted)
                    {
                        // 总是使用异步方式，避免死锁
                        _ = Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            try
                            {
                                ResetCountdownUiProperties();
                            }
                            catch (Exception uiEx)
                            {
                                Log.Error(uiEx, "[等待倒计时] 在UI线程上重置属性失败");
                            }
                        }, DispatcherPriority.Normal);
                    }
                    else
                    {
                        // Dispatcher不可用，直接执行
                        ResetCountdownUiProperties();
                    }
                }
                catch (Exception dispatcherEx)
                {
                    Log.Error(dispatcherEx, "[等待倒计时] 重置UI属性失败");
                    // 尝试使用现有的fire-and-forget方法作为fallback
                    try
                    {
                        _ = DispatchInvokeAsyncFireAndForget(() =>
                        {
                            try
                            {
                                ResetCountdownUiProperties();
                            }
                            catch (Exception asyncEx)
                            {
                                Log.Error(asyncEx, "[等待倒计时] 异步重置UI属性也失败");
                                // 最后的努力：直接重置状态
                                try
                                {
                                    IsWaitingForUploadResult = false;
                                    WaitingCountdownSeconds = 0;
                                    WaitingCountdownProgress = 0.0;
                                }
                                catch (Exception finalEx)
                                {
                                    Log.Error(finalEx, "[等待倒计时] 所有重置方式都失败");
                                }
                            }
                        });
                    }
                    catch (Exception fallbackEx)
                    {
                        Log.Error(fallbackEx, "[等待倒计时] 异步fallback也失败");
                    }
                }

                Log.Information(
                    "[等待倒计时] 停止等待PLC确认倒计时 - 之前状态: IsWaiting={WasWaiting}, TimerRunning={WasRunning}, Seconds={CurrentSeconds}, Progress={CurrentProgress:F1}%",
                    wasWaiting, wasRunning, currentSeconds, currentProgress);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[等待倒计时] StopWaitingCountdown出现异常");
                // 最后的努力：确保状态被重置
                try
                {
                    IsWaitingForUploadResult = false;
                    WaitingCountdownSeconds = 0;
                    WaitingCountdownProgress = 0.0;
                }
                catch (Exception resetEx)
                {
                    Log.Error(resetEx, "[等待倒计时] 最终状态重置也失败");
                }
            }
        }
    }

    /// <summary>
    ///     重置倒计时UI属性（在UI线程上执行）
    /// </summary>
    private void ResetCountdownUiProperties()
    {
        try
        {
            // 原子性地重置所有属性
            IsWaitingForUploadResult = false;
            WaitingCountdownSeconds = 0;
            WaitingCountdownProgress = 0.0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[等待倒计时] 重置倒计时UI属性时发生错误");
            // 如果UI线程操作失败，至少在后台线程上重置状态
            try
            {
                IsWaitingForUploadResult = false;
                WaitingCountdownSeconds = 0;
                WaitingCountdownProgress = 0.0;
                Log.Warning("[等待倒计时] 在异常情况下直接重置状态（UI可能不会更新）");
            }
            catch (Exception resetEx)
            {
                Log.Error(resetEx, "[等待倒计时] 即使直接重置状态也失败了");
            }
        }
    }

    // *** 新增: 清理包裹堆栈并释放资源 ***
    // *** Update signature to accept context string ***
    private void ClearPackageStack(string reason, string reasonContext, PackageInfo? excludePackage = null)
    {
        Log.Debug("{Context} 开始清理包裹堆栈. 原因: {Reason}", reasonContext, reason);
        var count = 0;
        while (_packageStack.TryPop(out var packageToDiscard))
        {
            // Create context for the discarded package
            var discardContext = $"[包裹{packageToDiscard.Index}|{packageToDiscard.Barcode}]";
            // *** Use discardContext and reasonContext in logs ***
            if (excludePackage != null && ReferenceEquals(packageToDiscard, excludePackage))
            {
                Log.Debug("{DiscardContext} ({ReasonContext}) 跳过释放排除的包裹 (通常是下一个要处理的)", discardContext, reasonContext);
                continue;
            }

            Log.Warning("{DiscardContext} ({ReasonContext}) 正在丢弃并释放堆栈中的包裹", discardContext, reasonContext);
            packageToDiscard.ReleaseImage();
            count++;
        }

        if (count > 0)
            Log.Information("{Context} 共清理了 {Count} 个堆栈中的包裹", reasonContext, count);
        else
            Log.Debug("{Context} 包裹堆栈已为空，无需清理.", reasonContext);
    }

    // *** 新增: 图像队列处理方法 - 顺序处理图像保存和上传，避免并发问题 ***
    private async Task ProcessImageQueueAsync()
    {
        Log.Information("[图像队列] 图像处理队列已启动，等待处理任务...");

        try
        {
            await foreach (var task in _imageProcessingChannel.Reader.ReadAllAsync(_imageProcessingCts.Token))
            {
                try
                {
                    Log.Debug("[图像队列] 开始处理图像任务: {Context}", task.PackageContext);

                    // 调用原有的图像处理方法
                    await HandleImageSavingAndUpload(task.Package, task.PackageContext, task.PackageId, task.ClonedImage);

                    Log.Debug("[图像队列] 图像任务处理完成: {Context}", task.PackageContext);

                    // 添加小延迟，避免处理过快
                    await Task.Delay(100, _imageProcessingCts.Token);
                }
                catch (OperationCanceledException) when (_imageProcessingCts.Token.IsCancellationRequested)
                {
                    Log.Information("[图像队列] 图像处理任务被取消");
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[图像队列] 处理图像任务时发生异常: {Context}", task.PackageContext);
                    // 继续处理下一个任务，不因为一个任务失败而停止整个队列
                }
            }
        }
        catch (OperationCanceledException) when (_imageProcessingCts.Token.IsCancellationRequested)
        {
            Log.Information("[图像队列] 图像处理队列被取消");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[图像队列] 图像处理队列出现严重错误");
        }

        Log.Information("[图像队列] 图像处理队列已停止");
    }

    // *** 新增: 队列图像处理任务的方法 ***
    private async Task QueueImageProcessingTaskAsync(PackageInfo package, string packageContext, int packageId, BitmapSource? clonedImage)
    {
        try
        {
            var task = new ImageProcessingTask(package, packageContext, packageId, clonedImage);

            // 将任务写入队列，如果队列满则等待
            await _imageProcessingChannel.Writer.WriteAsync(task, _imageProcessingCts.Token);

            Log.Debug("[图像队列] 图像处理任务已添加到队列: {Context}", packageContext);
        }
        catch (OperationCanceledException) when (_imageProcessingCts.Token.IsCancellationRequested)
        {
            Log.Warning("[图像队列] 图像处理任务被取消: {Context}", packageContext);
        }
        catch (ChannelClosedException)
        {
            Log.Warning("[图像队列] 图像处理队列已关闭，无法添加任务: {Context}", packageContext);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[图像队列] 添加图像处理任务到队列时发生异常: {Context}", packageContext);
        }
    }

    // *** 新增: 获取重量并处理包裹的方法 ***
    private void FetchWeightAndHandlePackageAsync(PackageInfo package, string packageContext)
    {
        Log.Debug("{Context} 开始获取重量", packageContext);
        try
        {
            var weightFromScale = _weight_service.GetLatestWeight();

            if (weightFromScale is > 0)
            {
                var weightInKg = weightFromScale.Value / 1000.0;
                package.SetWeight(weightInKg);
                Log.Information("{Context} 从重量称获取到重量: {WeightKg:F3}kg", packageContext, weightInKg);
            }
            else
            {
                var packageWeightOriginal = package.Weight;
                if (packageWeightOriginal <= 0)
                {
                    try
                    {
                        var weightSettings = _settings_service.LoadSettings<WeightSettings>();
                        var minimumWeight = weightSettings.MinimumWeight / 1000.0;
                        package.SetWeight(minimumWeight);
                        Log.Warning("{Context} 未获取到有效重量，使用最小重量: {MinWeight:F3}kg", packageContext, minimumWeight);
                    }
                    catch (Exception settingsEx)
                    {
                        Log.Error(settingsEx, "{Context} 加载重量设置失败，使用默认最小重量", packageContext);
                        const double defaultMinWeight = 0.1; // 100g 默认最小重量
                        package.SetWeight(defaultMinWeight);
                    }
                }
                else
                {
                    package.SetWeight(packageWeightOriginal);
                    Log.Debug("{Context} 重量称未返回有效重量，保留原始重量: {WeightKg:F3}kg", packageContext, packageWeightOriginal);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log.Warning("{Context} 重量查询被取消.", packageContext);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Context} 获取重量时出错.", packageContext);
            try
            {
                _ = _audioService.PlayPresetAsync(AudioType.SystemError);
            }
            catch (Exception audioEx)
            {
                Log.Error(audioEx, "{Context} 播放错误音频时发生异常", packageContext);
            }
        }

        // 检查重量称连接状态
        try
        {
            var weightService = _weight_service;
            if (!weightService.IsConnected)
            {
                Log.Warning("{Context} 重量称服务未连接或不可用，将使用默认重量", packageContext);
                try
                {
                    var weightSettings = _settings_service.LoadSettings<WeightSettings>();
                    var defaultWeight = weightSettings.MinimumWeight / 1000.0;
                    package.SetWeight(defaultWeight);
                }
                catch (Exception settingsEx)
                {
                    Log.Error(settingsEx, "{Context} 加载重量设置失败，使用默认重量", packageContext);
                    const double defaultMinWeight = 0.1; // 100g 默认最小重量
                    package.SetWeight(defaultMinWeight);
                }
            }
        }
        catch (Exception weightServiceEx)
        {
            Log.Error(weightServiceEx, "{Context} 检查重量称连接状态时发生异常", packageContext);
        }

        // 调用调度入口
        try
        {
            // *** Pass context ***
            HandleIncomingPackage(package, packageContext);
            Log.Debug("{Context} 重量获取完成，已提交到调度 HandleIncomingPackage", packageContext);
        }
        catch (Exception handleEx)
        {
            Log.Error(handleEx, "{Context} 调用HandleIncomingPackage时发生异常", packageContext);
            // 如果调度失败，至少释放图像资源
            try
            {
                package.ReleaseImage();
            }
            catch (Exception releaseEx)
            {
                Log.Error(releaseEx, "{Context} 释放图像资源时发生异常", packageContext);
            }
        }
    }
}