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
using DeviceService.DataSourceDevices.Camera.HuaRay;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Services.Dialogs;
using Serilog;
using SharedUI.Models;
using XinBeiYang.Models;
using XinBeiYang.Models.Communication;
using XinBeiYang.Services;
using System.Collections.Concurrent;

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
    private Brush _mainWindowBackgroundBrush = new SolidColorBrush(Colors.White); // Add new Brush property with default
    private bool _isPlcRejectWarningVisible; // PLC拒绝警告可见性标志
    private bool _isPlcAbnormalWarningVisible; // PLC异常警告可见性标志
    private CancellationTokenSource? _rejectionWarningCts;
    private BarcodeMode _barcodeMode = BarcodeMode.MultiBarcode; // 默认为多条码模式
    private int _selectedBarcodeModeIndex; // 新增：用于绑定 ComboBox 的 SelectedIndex
    
    // *** 新增: ViewModel 内部的包裹序号计数器 ***
    private int _viewModelPackageIndex;

    // *** 新增: 用于存储最近超时的条码前缀 ***
    private readonly ConcurrentDictionary<string, DateTime> _timedOutPrefixes = new();
    // 超时条目在缓存中的最大保留时间
    private static readonly TimeSpan TimedOutPrefixMaxAge = TimeSpan.FromSeconds(15);

    // 母条码正则表达式
    private static readonly Regex ParentBarcodeRegex = MyRegex();

    // Define new Brush constants (or create them inline)
    private static readonly Brush BackgroundWaiting = new SolidColorBrush(Color.FromArgb(0xAA, 0x21, 0x96, 0xF3)); // 蓝色 (增加透明度) - Changed from purple
    private static readonly Brush BackgroundSuccess = new SolidColorBrush(Color.FromArgb(0xAA, 0x4C, 0xAF, 0x50)); // 绿色 (增加透明度)
    private static readonly Brush BackgroundTimeout = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xC1, 0x07)); // 黄色 (增加透明度)
    private static readonly Brush BackgroundRejected = new SolidColorBrush(Color.FromArgb(0xAA, 0xF4, 0x43, 0x36)); // 红色 (增加透明度)
    private static readonly Brush BackgroundError = new SolidColorBrush(Color.FromArgb(0xAA, 0xF4, 0x43, 0x36)); // 红色 (增加透明度)

    public MainWindowViewModel(
        IDialogService dialogService,
        ICameraService cameraService,
        IAudioService audioService,
        IPlcCommunicationService plcCommunicationService,
        IJdWcsCommunicationService jdWcsCommunicationService,
        IImageStorageService imageStorageService,
        ISettingsService settingsService)
    {
        _dialogService = dialogService;
        _cameraService = cameraService;
        _audioService = audioService;
        _plcCommunicationService = plcCommunicationService;
        _jdWcsCommunicationService = jdWcsCommunicationService;
        _imageStorageService = imageStorageService;
        _settingsService = settingsService;

        // 初始化命令
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

        // 订阅包裹流
        _subscriptions.Add(_cameraService.PackageStream
            .ObserveOn(Scheduler.Default) // GroupBy and Buffer can run on a background thread
            .Do(pkg => Log.Debug("[Stream] 包裹通过过滤器: Barcode={Barcode}, Index={Index}, Timestamp={Timestamp}", pkg.Barcode, pkg.Index, DateTime.Now.ToString("O"))) // <-- 日志点 1: 过滤后
            .Where(FilterBarcodeByMode) // 根据当前条码模式过滤包裹
            .GroupBy(p => 
            {
                var prefix = GetBarcodePrefix(p.Barcode);
                Log.Debug("[Stream] 创建或加入分组: Prefix={Prefix}, Barcode={Barcode}, Index={Index}", prefix, p.Barcode, p.Index); // <-- 日志点 2: 分组时
                return prefix;
            })
            .SelectMany(group => group
                // Buffer for a short time (e.g., 2 seconds) or until 2 items arrive
                .Buffer(TimeSpan.FromMilliseconds(500), 2)
                // If Buffer emits an empty list, filter it out.
                .Where(buffer => buffer.Count > 0)
            )
            .ObserveOn(Scheduler.CurrentThread)
            .Subscribe(buffer =>
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
                        Log.Information("[Stream] 成功配对 (Timestamp: {Timestamp}): Prefix={Prefix}, Pkg1='{Barcode1}' (Index:{Index1}), Pkg2='{Barcode2}' (Index:{Index2})", 
                            currentTimestamp, prefix, p1.Barcode, p1.Index, p2.Barcode, p2.Index);
                        Log.Information("收到成对包裹，前缀 {Prefix}。准备合并: Index1={Index1}, Index2={Index2}", prefix, p1.Index, p2.Index);
                        packageToProcess = MergePackageInfo(p1, p2);
                        // Release images from original packages after merging
                        p1.ReleaseImage();
                        p2.ReleaseImage();
                        Log.Information("包裹合并完成: Index={MergedIndex}, Barcode='{MergedBarcode}'", packageToProcess.Index, packageToProcess.Barcode);
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
                            Log.Information("非多条码模式(母条码): 收到两个包裹，选择符合规则的母条码: {Barcode} (序号: {Index})，丢弃: {DiscardedBarcode}",
                                packageToKeep.Barcode, packageToKeep.Index, packageToDiscard.Barcode);
                        }
                        else // BarcodeMode == BarcodeMode.ChildBarcode
                        {
                            packageToKeep = !p1IsParent ? buffer[0] : buffer[1];
                            packageToDiscard = !p1IsParent ? buffer[1] : buffer[0];
                            Log.Information("非多条码模式(子条码): 收到两个包裹，选择符合规则的子条码: {Barcode} (序号: {Index})，丢弃: {DiscardedBarcode}",
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
                            Log.Warning("[Stream] 配对超时 (Timestamp: {Timestamp}): Prefix={Prefix}, ArrivedBarcode=\'{Barcode}\' (Index:{Index}). 将单独处理.",
                                currentTimestamp, currentPrefix, packageToProcess.Barcode, packageToProcess.Index);

                            // *** 记录此次超时 ***
                            Log.Information("[State] 记录配对超时前缀: {Prefix}", currentPrefix);
                            _timedOutPrefixes[currentPrefix] = DateTime.UtcNow; // 记录或更新超时时间
                            CleanupTimedOutPrefixes(TimedOutPrefixMaxAge); // 清理过期条目
                        }
                        else // Parent/Child 模式下的单个包裹 (已经被 Where 操作符正确过滤)
                        {
                            Log.Information("{Mode}模式下处理单个包裹: {Barcode} (序号: {Index})", GetBarcodeModeDisplayText(BarcodeMode), packageToProcess.Barcode, packageToProcess.Index);
                        }

                        break;
                    }
                }

                // *** 3. 调用后续处理 ***
                try
                {
                    _ = OnPackageInfo(packageToProcess); // Process the merged or single package
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理合并后/单个包裹时发生错误: {Barcode}", packageToProcess.Barcode);
                    // Clean up the package if processing failed early
                    packageToProcess.ReleaseImage();
                }
            }, ex => Log.Error(ex, "包裹流处理中发生未处理异常"))); // Add overall error handling for the stream

        // 订阅图像流
        _subscriptions.Add(_cameraService.ImageStream
            .ObserveOn(TaskPoolScheduler.Default) // 使用任务池调度器
            .Subscribe(imageData =>
            {
                try
                {
                    Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
                    {
                        try
                        {
                            // 更新UI
                            CurrentImage = imageData;
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "更新UI图像时发生错误");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理图像流时发生错误");
                }
            }));

        // 订阅相机服务启动事件
        if (_cameraService is HuaRayCameraService huaRayCameraServiceForEvent)
        {
             huaRayCameraServiceForEvent.ServiceStarted += OnCameraServiceStarted;
        }

        // 订阅华睿相机图像流
        if (_cameraService is HuaRayCameraService huaRayCameraService)
        {   
            _subscriptions.Add(huaRayCameraService.ImageStreamWithCameraId // 使用优化的流（现在提供BitmapSource）
                .ObserveOn(Scheduler.Default) // 在UI线程外处理转换，但WPF对象需要UI线程访问
                .Subscribe(imageData =>
                {
                    var receivedBitmapSource = imageData.bitmapSource; // 直接接收BitmapSource
                    var cameraId = imageData.cameraId;

                    try
                    {
                        // 跳过条码图像处理以提高性能
                        // 直接使用接收到的BitmapSource
                        var finalBitmapSource = receivedBitmapSource;

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
                                // 使用原始图像（无条码覆盖）更新UI属性
                                targetCamera.CurrentImage = finalBitmapSource;
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
                })); // 结束Subscribe
        }
        else
        {
             Log.Warning("相机服务不是 HuaRayCameraService，无法订阅带ID的图像流");
             // 考虑订阅通用的ICameraService.ImageStream
             // 并在需要时将Rgba32转换为BitmapSource作为备选方案
        }
    }

    /// <summary>
    /// 相机服务启动事件处理
    /// </summary>
    private void OnCameraServiceStarted()
    {
        Log.Information("相机服务已启动，开始初始化相机列表...");
        // 确保初始化在UI线程上进行
        Application.Current.Dispatcher.Invoke(InitializeCameras);
    }

    // 统一更新设备状态的方法
    private void UpdateDeviceStatus(string statusText, string description, string color)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            try
            {
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

    // 处理PLC设备状态变更事件 - 统一处理所有状态变更
    private void OnPlcDeviceStatusChanged(object? sender, DeviceStatusCode statusCode)
    {
        // 获取对应的状态信息
        var statusText = GetDeviceStatusDisplayText(statusCode);
        var description = GetDeviceStatusDescription(statusCode);
        var color = GetDeviceStatusColor(statusCode);

        // 通过统一方法更新状态
        UpdateDeviceStatus(
            statusText,
            description,
            color
        );

        // 如果PLC状态恢复正常，隐藏PLC异常警告
        if (statusCode != DeviceStatusCode.Normal) return;
        if (!IsPlcAbnormalWarningVisible) return;
        Log.Information("PLC状态恢复正常，隐藏PLC异常警告。");
        IsPlcAbnormalWarningVisible = false;
        // 如果PLC状态变为非正常，则不需要在此处显示警告，
        // 而是在OnPackageInfo收到包裹时判断并显示。
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
            DeviceStatusCode.Disconnected => "#F44336", // 红色
            _ => "#F44336" // 红色
        };
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
    /// 主窗口内容区域背景画刷
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
        if (string.IsNullOrEmpty(package.Barcode) || string.Equals(package.Barcode, "noread", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        
        var isParentBarcode = ParentBarcodeRegex.IsMatch(package.Barcode);
        
        return BarcodeMode switch
        {
            BarcodeMode.MultiBarcode => true, // 多条码模式不过滤
            BarcodeMode.ParentBarcode => isParentBarcode, // 母条码模式只处理符合正则的条码
            BarcodeMode.ChildBarcode => !isParentBarcode, // 子条码模式只处理不符合正则的条码
            _ => true
        };
    }

    /// <summary>
    /// 初始化相机列表
    /// </summary>
    private void InitializeCameras()
    {
        try
        {
            // 存储现有的CameraDisplayInfo以便稍后可能恢复状态（例如，用户设置）
            Cameras.Clear();

            if (_cameraService is HuaRayCameraService huaRayService)
            {
                var huaRayCameras = huaRayService.GetCameras();
                Log.Information("获取到 {Count} 个华睿相机", huaRayCameras.Count);
                if (huaRayCameras.Count > 0)
                {
                    CalculateOptimalLayout(huaRayCameras.Count);
                    var cameraIndex = 0;
                    foreach (var camera in huaRayCameras)
                    {
                        var (row, column, rowSpan, columnSpan) = GetCameraPosition(cameraIndex, huaRayCameras.Count);
                        
                        // 构造与事件格式匹配的相机ID（供应商:序列号）
                        // 如果供应商或序列号缺失则使用备选
                        var constructedCameraId = string.IsNullOrWhiteSpace(camera.camDevVendor) || string.IsNullOrWhiteSpace(camera.camDevSerialNumber)
                                                   ? camera.camDevID // 如果部分缺失则使用原始ID
                                                   : $"{camera.camDevVendor}:{camera.camDevSerialNumber}"; 
            
                        // 如果构造的ID仍然为空，可能使用名称作为最后的备选？不太可能匹配事件。
                        if (string.IsNullOrEmpty(constructedCameraId))
                        {
                             Log.Warning("相机信息缺少供应商或序列号，且camDevID也为空。无法为索引{Index}的相机构造可靠ID。使用备选方案。", cameraIndex);
                             // 决定使用备选ID - 可能是索引？或跳过添加？使用索引作为备选。
                             constructedCameraId = $"fallback_{cameraIndex}"; 
                        }
                        
                        var cameraName = string.IsNullOrEmpty(camera.camDevSerialNumber) 
                            ? $"相机 {cameraIndex + 1}" 
                            : $"{camera.camDevModelName} {camera.camDevSerialNumber}"; // 保持名称不变

                        // 使用构造的相机ID进行日志记录和CameraDisplayInfo
                        Log.Information("添加相机占位符: ID={ID}, 名称={Name}, 行={Row}, 列={Column}", constructedCameraId, cameraName, row, column);
                        
                        var displayInfo = new CameraDisplayInfo
                        {
                            CameraId = constructedCameraId, // 使用构造的ID
                            CameraName = cameraName,  
                            IsOnline = true, // 待办：获取实际状态？可能从GetCamerasStatus获取？
                            Row = row,
                            Column = column,
                            RowSpan = rowSpan,
                            ColumnSpan = columnSpan,
                            // CurrentImage将由流设置
                        };
                        
                        // 如有必要，从existingDisplayInfo恢复其他设置
                        
                        Cameras.Add(displayInfo);
                        cameraIndex++;
                    }
                    return;
                }
            }
            
            Log.Warning("未获取到华睿相机信息或非华睿相机，添加默认相机占位符");
            CalculateOptimalLayout(1); // 1个相机的布局
            Cameras.Add(new CameraDisplayInfo
            {
                CameraId = "0", CameraName = "主相机", IsOnline = _cameraService.IsConnected,
                Row = 0, Column = 0, RowSpan = 1, ColumnSpan = 1
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化相机列表时发生错误");
            if (Cameras.Count == 0)
            {   
                CalculateOptimalLayout(1);
                Cameras.Add(new CameraDisplayInfo { /* ... 默认值 ... */ });
            }
        }
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
        switch (cameraCount)
        {
            // 特殊情况处理
            case 1:
                // 单个相机占据整个网格
                return (0, 0, 1, 1);
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
              new DeviceStatusInfo("相机", "Camera24", _cameraService.IsConnected ? "已连接" : "已断开", _cameraService.IsConnected ? "#4CAF50" : "#F44336")
        ];
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

    private void OnCameraConnectionChanged(string? cameraId, bool isConnected)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var cameraStatus = DeviceStatuses.FirstOrDefault(static s => s.Name == "相机");
            if (cameraStatus == null) return;

            cameraStatus.Status = isConnected ? "已连接" : "已断开";
            cameraStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
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

    private async Task OnPackageInfo(PackageInfo package)
    {
        // *** 新增: 使用 ViewModel 计数器覆盖包裹序号 ***
        var assignedIndex = Interlocked.Increment(ref _viewModelPackageIndex);
        package.Index = assignedIndex;
        Log.Information("包裹进入处理流程，分配 ViewModel 序号: {ViewModelIndex} (原始创建序号: {OriginalIndex})", assignedIndex, package.Index /* 这里实际已被覆盖，记录原始值可能需要修改Create或传入 */);
        // 如果需要记录原始 Create() 生成的 Index，PackageInfo.Create 需要调整或在流中传递

        try
        {
            // 1. 检查PLC状态是否异常
            if (DeviceStatusText != "正常")
            {
                // 只在PLC拒绝警告未显示时显示PLC异常警告
                if (!IsPlcRejectWarningVisible)
                {
                    Log.Warning("PLC状态异常 ({Status})，显示警告并忽略新包裹: {Barcode} (序号: {Index})", DeviceStatusText, package.Barcode, package.Index);
                    IsPlcAbnormalWarningVisible = true;
                    // 播放PLC未连接音效
                    _ = _audioService.PlayPresetAsync(AudioType.PlcDisconnected);
                    return; // PLC 状态异常，直接返回
                }

                Log.Warning("PLC状态异常 ({Status}) 但PLC拒绝警告可能已显示，忽略新包裹: {Barcode} (序号: {Index})", DeviceStatusText, package.Barcode, package.Index);
                return; // PLC 状态异常，直接返回
            }

            // 2. 重置PLC异常警告（如果之前显示了）
            // 因为能执行到这里，说明PLC状态正常
            if (IsPlcAbnormalWarningVisible)
            {
                IsPlcAbnormalWarningVisible = false;
            }

            // 检查是否未读取到条码
            if (string.Equals(package.Barcode, "noread", StringComparison.OrdinalIgnoreCase))
            {
                Log.Information("收到 'noread' 条码，跳过处理");
                // 播放等待扫码音效
                _ = _audioService.PlayPresetAsync(AudioType.WaitingScan);
                return;
            }

            // 在UI线程上更新条码和基础信息，并将状态设置为等待上包
            Application.Current.Dispatcher.Invoke(() =>
            {
                CurrentBarcode = package.Barcode;
                UpdatePackageInfoItemsBasic(package); // 提前更新基础信息
                UpdatePackageInfoStatusInitial(package); // 设置初始状态为等待上包
            });

            // 在收到条码时播放等待上包音效
            _ = _audioService.PlayPresetAsync(AudioType.WaitingForLoading);

            // 向PLC发送上传请求 - 直接使用 PackageInfo 的条码 (可能是合并后的)
            var plcRequestTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Log.Information("向PLC发送上传请求: Barcode={PlcBarcode}, Weight={Weight}, Length={L}, Width={W}, Height={H}",
                package.Barcode, 
                package.Weight, package.Length, package.Width, package.Height);

            var (isSuccess, isTimeout, commandId, packageId) = await _plcCommunicationService.SendUploadRequestAsync(
                (float)package.Weight,
                (float)(package.Length ?? 0),
                (float)(package.Width ?? 0),
                (float)(package.Height ?? 0),
                package.Barcode,
                string.Empty,
                (ulong)plcRequestTimestamp);

            // 处理PLC响应
            if (!isSuccess)
            {
                if (isTimeout) // 4. 上包结果为1时（超时）
                {
                    Log.Warning("包裹 {Barcode}(序号:{Index}) 上包超时，CommandId={CommandId}", package.Barcode, package.Index, commandId);
                    package.SetStatus(PackageStatus.Error,$"上包超时 (序号: {package.Index})"); // UpdateUiFromPackage使用的错误消息
                    // 播放上包超时音效
                    _ = _audioService.PlayPresetAsync(AudioType.LoadingTimeout);
                }
                else // 3. ACK结果反馈为1驳回请求时
                {
                    Log.Warning("包裹 {Barcode}(序号:{Index}) 上包请求被拒绝，CommandId={CommandId}", package.Barcode, package.Index, commandId);
                    package.SetStatus(PackageStatus.Error,$"上包拒绝 (序号: {package.Index})"); // UpdateUiFromPackage使用的错误消息
                    // 播放拒绝上包音效
                    _ = _audioService.PlayPresetAsync(AudioType.LoadingRejected);
                }
            }
            else // PLC上传请求成功（ACK = 0）
            {
                package.SetStatus(PackageStatus.Success, "上包完成");
                Log.Information("包裹 {Barcode}(序号:{Index}) 上包成功，CommandId={CommandId}, 包裹流水号={PackageId}", package.Barcode, package.Index, commandId, packageId);
                // 播放上包成功音效
                _ = _audioService.PlayPresetAsync(AudioType.LoadingSuccess);

                // 本地保存图像
                if (package.Image != null)
                {
                    Log.Debug("准备克隆并冻结图像用于保存: Barcode={Barcode}, Index={Index}", package.Barcode, package.Index);
                    BitmapSource? imageToSave = null;
                    try
                    {
                        // 克隆图像
                        var clonedImage = package.Image.Clone();
                        // 冻结克隆，使其线程安全
                        if (clonedImage.CanFreeze)
                        {
                            clonedImage.Freeze();
                            imageToSave = clonedImage;
                            Log.Debug("图像克隆并冻结成功: Barcode={Barcode}", package.Barcode);
                        }
                        else
                        {
                            Log.Warning("克隆的图像无法冻结，仍尝试使用克隆进行保存: Barcode={Barcode}", package.Barcode);
                            imageToSave = clonedImage; // 即使不能冻结，也尝试使用克隆
                        }
                    }
                    catch (Exception cloneEx)
                    {
                        Log.Error(cloneEx, "克隆或冻结图像时发生错误: Barcode={Barcode}. 无法保存图像。", package.Barcode);
                    }

                    // 只有在克隆（和可选的冻结）成功后才尝试保存
                    if (imageToSave != null)
                    {
                        var imagePath = await _imageStorageService.SaveImageAsync(imageToSave, package.Barcode, package.CreateTime); // 存储已保存的图像路径
                        if (imagePath != null)
                        {
                            package.ImagePath = imagePath; // 如果需要，将路径存储在包裹信息中
                            Log.Information("包裹图像保存成功: Path={ImagePath}", imagePath);

                            // 上传图像URL到京东WCS
                            if (_jdWcsCommunicationService.IsConnected)
                            {
                                 Log.Information("开始上传图片地址到京东WCS: TaskNo={TaskNo}", packageId);
                                 var uploadSuccess = await _jdWcsCommunicationService.UploadImageUrlsAsync(
                                    packageId,                      // taskNo（PLC包裹ID）
                                    [package.Barcode],             // 条码列表
                                    [],                           // 矩阵条码列表（暂时为空）
                                    [imagePath],                  // 图像URL列表
                                    (long)(package.CreateTime.ToUniversalTime() - DateTimeOffset.UnixEpoch).TotalMilliseconds // 时间戳
                                );
                                if(uploadSuccess)
                                {
                                    Log.Information("图片地址上传成功到京东WCS: TaskNo={TaskNo}, Path={Path}", packageId, imagePath);
                                }
                                else
                                {
                                    Log.Warning("图片地址上传失败到京东WCS: TaskNo={TaskNo}, Path={Path}", packageId, imagePath);
                                }
                            }
                            else
                            {
                                Log.Warning("京东WCS未连接，无法上传图片地址: TaskNo={TaskNo}, Path={Path}", packageId, imagePath);
                                // 使用SetStatus方法更新状态并添加自定义显示文本
                                var currentStatus = package.Status;
                                var currentDisplay = package.StatusDisplay + " [WCS未连接]";
                                package.SetStatus(currentStatus, currentDisplay);
                                 // 考虑排队或添加错误信息？
                            }
                        }
                        else
                        {
                            Log.Error("包裹图像保存失败: Barcode={Barcode}, Index={Index}", package.Barcode, package.Index);
                            // 向包裹添加错误信息？
                            var currentStatus = package.Status;
                            var currentDisplay = package.StatusDisplay + " [图像保存失败]";
                            package.SetStatus(currentStatus, currentDisplay);
                        }
                    }
                }
                else
                {
                    Log.Warning("包裹信息中无图像可保存: Barcode={Barcode}, Index={Index}", package.Barcode, package.Index);
                     // 向包裹添加信息？
                    var currentStatus = package.Status;
                    var currentDisplay = package.StatusDisplay + " [无图像]";
                    package.SetStatus(currentStatus, currentDisplay);
                }
            }
            // 更新UI（此包裹的最终状态）
            UpdateUiFromPackage(package); // 使用重构的方法
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹 {Barcode}(序号:{Index}) 时发生错误", package.Barcode, package.Index);
            package.SetStatus(PackageStatus.Error,$"处理失败：{ex.Message} (序号: {package.Index})");
            UpdateUiFromPackage(package); // 即使出错也更新UI
            // 对于一般错误，继续使用系统错误音效
            _ = _audioService.PlayPresetAsync(AudioType.SystemError);
        }
        finally
        {
           package.ReleaseImage();
           Log.Debug("包裹处理完成，图像资源已释放: Index={Index}", package.Index);
           // Consider if _rejectionWarningCts needs cancellation here in case of exceptions
        }
    }

    /// <summary>
    /// 合并UI更新的辅助方法
    /// </summary>
    private void UpdateUiFromPackage(PackageInfo package)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            // 更新最后一个包裹的状态标志
            LastPackageWasSuccessful = string.IsNullOrEmpty(package.ErrorMessage);
            // CurrentBarcode 已提前更新
            UpdatePackageInfoItemsStatusFinal(package); // 更新最终状态信息
            UpdatePackageHistory(package);
            UpdateStatistics(package);
        });
    }

    /// <summary>
    /// 更新包裹信息项的最终状态部分
    /// </summary>
    private void UpdatePackageInfoItemsStatusFinal(PackageInfo package)
    {
        var statusItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "状态");
        if (statusItem == null) return;

        if (string.IsNullOrEmpty(package.ErrorMessage)) // 成功情况（isSuccess为真）
        {
            // 4. 上包结果为0时，状态显示"上包完成"
            statusItem.Value = $"上包完成 (序号: {package.Index})";
            // 如果在Information中可用，提取PackageId供描述使用
            statusItem.Description = $"PLC 包裹流水号: {GetPackageIdFromInformation(package.StatusDisplay)}";
            MainWindowBackgroundBrush = BackgroundSuccess; // Update new Brush property
            statusItem.StatusColor = "#4CAF50"; // 绿色，表示成功
            // 成功时确保警告关闭，并取消超时任务
            IsPlcRejectWarningVisible = false;
            _rejectionWarningCts?.Cancel();
        }
        else // 错误情况（isSuccess为假）
        {
            // 值已由package.SetError(...)正确设置
            // 示例："上包超时 (序号: X)"、"上包拒绝 (序号: X)"
            statusItem.Value = package.ErrorMessage;

            // 根据错误类型设置描述和颜色
            if (package.ErrorMessage.StartsWith("上包超时"))
            {
                statusItem.Description = "上包请求未收到 PLC 响应";
                MainWindowBackgroundBrush = BackgroundTimeout; // Update new Brush property
                statusItem.StatusColor = "#FFC107"; // 黄色，表示超时
                // 超时时关闭警告，并取消超时任务
                IsPlcRejectWarningVisible = false;
                _rejectionWarningCts?.Cancel();
            }
            else if (package.ErrorMessage.StartsWith("上包拒绝"))
            {
                statusItem.Description = "PLC 拒绝了上包请求";
                MainWindowBackgroundBrush = BackgroundRejected; // Update new Brush property
                statusItem.StatusColor = "#F44336"; // 红色，表示拒绝

                // 取消上一个超时任务（如果有）
                _rejectionWarningCts?.Cancel();
                _rejectionWarningCts?.Dispose();
                _rejectionWarningCts = new CancellationTokenSource();

                IsPlcRejectWarningVisible = true; // 显示警告
                IsPlcAbnormalWarningVisible = false; // 确保PLC异常警告关闭

                // 启动超时隐藏任务
                _ = StartRejectionWarningTimeoutAsync(_rejectionWarningCts.Token);

            }
            else // 通用错误
            {
                statusItem.Description = $"处理失败 (序号: {package.Index})";
                MainWindowBackgroundBrush = BackgroundError; // Update new Brush property
                statusItem.StatusColor = "#F44336"; // 红色，表示错误
                // 其他错误确保警告关闭，并取消超时任务
                IsPlcRejectWarningVisible = false;
                _rejectionWarningCts?.Cancel();
            }
        }
    }

    /// <summary>
    /// 设置包裹信息项的初始状态为"等待上包"
    /// </summary>
    private void UpdatePackageInfoStatusInitial(PackageInfo package)
    {
        var statusItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "状态");
        if (statusItem == null) return;

        statusItem.Value = $"等待上包 (序号: {package.Index})";
        statusItem.Description = "正在请求PLC...";
        // 使用等待状态的颜色
        MainWindowBackgroundBrush = BackgroundWaiting; // Update new Brush property
        
        // 更新状态项的颜色
        statusItem.StatusColor = "#2196F3"; // 蓝色，表示等待中
    }

    /// <summary>
    /// 更新非状态项的新辅助方法，可在异步操作之前调用
    /// </summary>
    private void UpdatePackageInfoItemsBasic(PackageInfo package)
    {
        var weightItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "重量");
        if (weightItem != null)
        {
            weightItem.Value = package.Weight.ToString(CultureInfo.InvariantCulture);
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
    }

    /// <summary>
    /// 更新包裹历史记录
    /// </summary>
    private void UpdatePackageHistory(PackageInfo package)
    {
        try
        {
            // 限制历史记录数量，保持最新的100条记录
            const int maxHistoryCount = 1000;

            // 添加自定义状态显示文本（如果需要）
            if (string.IsNullOrEmpty(package.StatusDisplay))
            {
                var customDisplay = string.IsNullOrEmpty(package.ErrorMessage)
                    ? $"处理成功 (序号: {package.Index})"
                    : $"{package.ErrorMessage} (序号: {package.Index})";
                package.SetStatus(package.Status, customDisplay);
            }

            // 添加到历史记录开头
            PackageHistory.Insert(0, package);

            // 如果超出最大数量，移除多余的记录
            while (PackageHistory.Count > maxHistoryCount) PackageHistory.RemoveAt(PackageHistory.Count - 1);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新历史包裹列表时发生错误");
        }
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
        try
        {
            // 更新总包裹数
            var totalItem = StatisticsItems.FirstOrDefault(static x => x.Label == "总包裹数");
            if (totalItem != null)
            {
                var total = int.Parse(totalItem.Value) + 1;
                totalItem.Value = total.ToString();
            }

            // 更新成功/失败数
            var isSuccess = string.IsNullOrEmpty(package.ErrorMessage);
            var targetLabel = isSuccess ? "成功数" : "失败数";
            var statusItem = StatisticsItems.FirstOrDefault(x => x.Label == targetLabel);
            if (statusItem != null)
            {
                var count = int.Parse(statusItem.Value) + 1;
                statusItem.Value = count.ToString();
            }

            // 更新处理速率（每小时包裹数）
            var speedItem = StatisticsItems.FirstOrDefault(static x => x.Label == "处理速率");
            if (speedItem == null || PackageHistory.Count < 2) return;
            // 获取最早和最新的包裹时间差
            var latestTime = PackageHistory[0].CreateTime;
            var earliestTime = PackageHistory[^1].CreateTime;
            var timeSpan = latestTime - earliestTime;

            if (!(timeSpan.TotalSeconds > 0)) return;
            // 计算每小时处理数量
            var hourlyRate = PackageHistory.Count / timeSpan.TotalHours;
            speedItem.Value = Math.Round(hourlyRate).ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新统计信息时发生错误");
        }
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
                Log.Warning("UploadTimeoutSeconds 配置不大于 0 ({Seconds})，PLC拒绝警告将不会自动隐藏", timeoutSeconds);
                return; // 不启动超时
            }

            Log.Information("PLC拒绝警告将在 {TimeoutSeconds} 秒后自动隐藏", timeoutSeconds);
            await Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), token);

            // 如果没有被取消，则隐藏警告
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (token.IsCancellationRequested) return; // 再次检查，以防在Invoke排队时被取消
                IsPlcRejectWarningVisible = false;
                Log.Information("PLC拒绝警告已超时自动隐藏");
            });
        }
        catch (OperationCanceledException)
        {
            // 预期异常，当被外部取消时（例如，成功处理或手动清除）
            Log.Debug("PLC拒绝警告超时任务被取消");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理PLC拒绝警告超时时出错");
            // 发生意外错误时，也尝试隐藏警告以防卡住
             Application.Current.Dispatcher.Invoke(() => IsPlcRejectWarningVisible = false);
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

        var mergedPackage = PackageInfo.Create(); // 获取新的序号和初始状态

        // 使用较早的创建时间
        mergedPackage.SetTriggerTimestamp(basePackage.CreateTime < suffixPackage.CreateTime ? basePackage.CreateTime : suffixPackage.CreateTime);

        // 合并条码: prefix;suffix-barcode
        var prefix = GetBarcodePrefix(basePackage.Barcode);
        var combinedBarcode = $"{prefix},{(suffixPackage.Barcode)}";
        mergedPackage.SetBarcode(combinedBarcode);

        // 优先使用 basePackage 的数据，如果缺失则用 suffixPackage 的
        mergedPackage.SetSegmentCode(basePackage.SegmentCode);
        mergedPackage.SetWeight(basePackage.Weight > 0 ? basePackage.Weight : suffixPackage.Weight);

        // 优先使用 basePackage 的尺寸
        if (basePackage is { Length: > 0, Width: not null, Height: not null })
        {
            mergedPackage.SetDimensions(basePackage.Length.Value, basePackage.Width.Value, basePackage.Height.Value);
        }
        else if (suffixPackage is { Length: > 0, Width: not null, Height: not null })
        {
             mergedPackage.SetDimensions(suffixPackage.Length.Value, suffixPackage.Width.Value, suffixPackage.Height.Value);
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
                 imagePathToUse = sourceImagePath; // 使用关联的路径
            }
            catch (Exception imgEx)
            {
                Log.Error(imgEx, "为合并后的包裹克隆或冻结图像时出错: OriginalBarcode={BaseBarcode}", basePackage.Barcode);
                imageToUse = null; // 出错则不设置图像
                imagePathToUse = null;
            }
        }

        mergedPackage.SetImage(imageToUse, imagePathToUse); // 设置克隆/冻结的图像和路径

        // 初始状态为 Created
        Log.Debug("合并后的包裹信息: Index={Index}, Barcode='{Barcode}', Weight={Weight}, Dimensions='{Dims}', ImageSet={HasImage}, ImagePath='{Path}'",
            mergedPackage.Index, mergedPackage.Barcode, mergedPackage.Weight, mergedPackage.VolumeDisplay, mergedPackage.Image != null, mergedPackage.ImagePath ?? "N/A");


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
                // 取消订阅事件
                _cameraService.ConnectionChanged -= OnCameraConnectionChanged;
                _plcCommunicationService.DeviceStatusChanged -= OnPlcDeviceStatusChanged;
                _jdWcsCommunicationService.ConnectionChanged -= OnJdWcsConnectionChanged;
                // 取消订阅ServiceStarted事件
                if (_cameraService is HuaRayCameraService huaRayCameraServiceForEvent)
                {
                     huaRayCameraServiceForEvent.ServiceStarted -= OnCameraServiceStarted;
                }

                // 释放订阅
                foreach (var subscription in _subscriptions) subscription.Dispose();

                _subscriptions.Clear();

                // 停止定时器
                _timer.Stop();

                // 取消并释放拒绝警告的CTS
                _rejectionWarningCts?.Cancel();
                _rejectionWarningCts?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "释放资源时发生错误");
            }

        _disposed = true;
    }

    [GeneratedRegex(@"^(JD[0-9A-GI-MO-RT-Z]{12}\d)([-,N])([1-9][0-9]{0,5})([-,S])([0-9A-GI-MO-RT-Z]{1,6})([-,H]\w{0,8})?|^(\w{1,5}\d{1,20})([-,N])([1-9][0-9]{0,5})([-,S])([0-9A-GI-MO-RT-Z]{1,6})([-,H]\w{0,8})?|^([Zz][Yy])[A-Za-z0-9]{13}[-][1-9][0-9]*[-][1-9][0-9]*[-]?$|^([A-Z0-9]{8,})(-|N)([1-9]d{0,2})(-|S)([1-9]d{0,2})([-|H][A-Za-z0-9]*)$|^AK.*$|^BX.*$|^BC.*$|^AD.*$", RegexOptions.Compiled)]
    private static partial Regex MyRegex();

    // *** 新增: 清理过期的超时前缀记录 ***
    private void CleanupTimedOutPrefixes(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var keysToRemove = _timedOutPrefixes.Where(kvp => kvp.Value < cutoff).Select(kvp => kvp.Key).ToList();
        foreach (var key in keysToRemove)
        {
            if (_timedOutPrefixes.TryRemove(key, out _))
            {
                Log.Debug("[State] 清理了过期的超时前缀: {Prefix}", key);
            }
        }
    }
}