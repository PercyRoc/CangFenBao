using System.Collections.ObjectModel;
using System.IO;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Camera.Interface;
using Camera.Services;
using Camera.Services.Implementations.Hikvision.Security;
using Camera.Services.Implementations.Hikvision.Volume;
using Common.Models.Package;
using Common.Services.Settings;
using Rookie.Services;
using Serilog;
using SharedUI.Models;
using Sorting_Car.Models;
using Sorting_Car.Services;
using Weight.Services;
using LocalWeightSettings = Weight.Models.Settings.WeightSettings;
using System.Diagnostics;
using Camera.Models.Settings;

namespace Rookie.ViewModels.Windows;

public class MainWindowViewModel : BindableBase, IDisposable
{
    private static long _globalPackageIndex;
    private static int _nextCyclicChute; // 新增: 用于循环格口分配的静态计数器

    private readonly IDialogService _dialogService;
    private readonly IRookieApiService _rookieApiService;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly DispatcherTimer _timer;
    private readonly ISettingsService _settingsService;
    private readonly CarSequenceSettings _carSequenceSettings;

    private string _currentBarcode = string.Empty;
    private BitmapSource? _currentImage;
    private BitmapSource? _eventPackageImage;
    private bool _disposed;
    private SystemStatus _systemStatus = new();

    private long _totalPackageCount;
    private long _successPackageCount;
    private long _failedPackageCount;
    private long _peakRate;

    // 移除了轮询相关的常量和旧的重量缓存字段
    // private const int SupplementDataPollIntervalMs = 50;
    // private (double WeightInKg, DateTime Timestamp)? _lastReceivedValidWeight; // 移除
    // private volatile Tuple<double, DateTime>? _logicOrientedWeightCache; // 移除

    // 新增: 稳定重量队列相关定义 (类似SangNeng)
    private readonly struct StableWeightEntry(double weightKg, DateTime timestamp)
    {
        public double WeightKg { get; } = weightKg;
        public DateTime Timestamp { get; } = timestamp;
    }
    private readonly Queue<StableWeightEntry> _stableWeightQueue = new();
    private const int MaxStableWeightQueueSize = 100; // 与SangNeng保持一致或设为可配置
    private readonly List<double> _rawWeightBuffer = [];
    // StabilityCheckSamples, StabilityThresholdGrams 将从 LocalWeightSettings 加载
    // IntegrationTimeMs (from LocalWeightSettings) 将用作队列查询窗口

    private readonly HikvisionSecurityCameraService _securityCameraService;
    private readonly HikvisionVolumeCameraService _volumeCameraService;
    private readonly ICameraService _industrialCameraService;
    private readonly IWeightService _weightService;
    private readonly CarSortService _carSortService;
    private readonly CarSortingService _carSortingService;

    private volatile bool _isProcessingPackage;

    // 移除了 IsWeightStable 辅助函数，因为稳定性检查逻辑将在订阅中处理

    public MainWindowViewModel(
        IDialogService dialogService,
        ISettingsService settingsService,
        IRookieApiService rookieApiService,
        IWeightService weightService,
        CarSortService carSortService,
        CarSortingService carSortingService,
        ICameraService industrialCameraService,
        CameraDataProcessingService cameraDataProcessingService,
        HikvisionSecurityCameraService securityCameraService,
        HikvisionVolumeCameraService volumeCameraService)
    {
        _dialogService = dialogService;
        _settingsService = settingsService;
        _rookieApiService = rookieApiService;
        _weightService = weightService;
        _carSortService = carSortService;
        _carSortingService = carSortingService;
        _industrialCameraService = industrialCameraService;
        _securityCameraService = securityCameraService;
        _volumeCameraService = volumeCameraService;

        _carSequenceSettings = _settingsService.LoadSettings<CarSequenceSettings>();

        OpenSettingsCommand = new DelegateCommand(ExecuteOpenSettings);
        OpenHistoryCommand = new DelegateCommand(ExecuteOpenHistory);

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        InitializeDeviceStatuses();
        InitializeStatisticsItems();
        InitializePackageInfoItems();

        _securityCameraService.ConnectionChanged += OnSecurityCameraConnectionChanged;
        _volumeCameraService.ConnectionChanged += OnVolumeCameraConnectionChanged;
        _industrialCameraService.ConnectionChanged += OnIndustrialCameraConnectionChanged;
        _weightService.ConnectionChanged += OnWeightServiceConnectionChanged;
        _carSortingService.ConnectionChanged += OnCarSerialPortConnectionChanged;

        _subscriptions.Add(_industrialCameraService.ImageStreamWithId
            .ObserveOn(TaskPoolScheduler.Default)
            .Subscribe(imageData =>
            {
                try
                {
                    imageData.Image.Freeze();
                    Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
                    {
                        try
                        {
                            EventPackageImage = imageData.Image;
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "从工业相机实时流更新UI线程上的EventPackageImage时出错。");
                        }
                    });
                }   
                catch (Exception ex)
                {
                    Log.Error(ex, "处理工业相机实时流中的图像时出错。");
                }
            }, ex => Log.Error(ex, "工业相机实时图像流订阅出错。")));

        // 订阅重量数据流 (用于UI实时显示)
        _subscriptions.Add(_weightService.WeightDataStream
            .Subscribe(weightData =>
            {
                Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
                {
                    try
                    {
                        var weightItem = PackageInfoItems.FirstOrDefault(i => i.Label == "Weight");
                        if (weightItem == null) return;
                        weightItem.Value = weightData != null ? weightData.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) : "0.00";
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "更新实时包裹重量信息时出错。");
                    }
                });
            }, ex => Log.Error(ex, "UI重量数据流订阅出错。")));

        // 新增: 专门为稳定重量处理订阅重量数据流 (后台线程)
        _subscriptions.Add(_weightService.WeightDataStream
            .ObserveOn(TaskPoolScheduler.Default) // 在后台线程处理
            .Where(wd => wd != null) // 确保数据不为null
            .Subscribe(weightData =>
            {
                var weightSettings = _settingsService.LoadSettings<LocalWeightSettings>();
                var stabilityCheckSamples = weightSettings.StabilityCheckSamples > 0 ? weightSettings.StabilityCheckSamples : 5;
                var stabilityThresholdGrams = weightSettings.StabilityThresholdGrams > 0 ? weightSettings.StabilityThresholdGrams : 20.0;

                // weightData.Value 是 double (kg), 稳定性判断通常用 g
                double currentRawWeightGrams = weightData!.Value * 1000.0;
                var currentTimestamp = weightData.Timestamp;

                _rawWeightBuffer.Add(currentRawWeightGrams);
                if (_rawWeightBuffer.Count > stabilityCheckSamples)
                {
                    _rawWeightBuffer.RemoveAt(0);
                }

                if (_rawWeightBuffer.Count != stabilityCheckSamples) return;
                double minWeightInWindow = _rawWeightBuffer.Min();
                double maxWeightInWindow = _rawWeightBuffer.Max();

                if (!((maxWeightInWindow - minWeightInWindow) < stabilityThresholdGrams)) return;
                double stableAverageWeightGrams = _rawWeightBuffer.Average();
                double stableWeightKg = stableAverageWeightGrams / 1000.0;

                if (stableWeightKg <= 0) // 新增检查: 稳定重量必须大于0
                {
                    return;
                }

                var stableEntry = new StableWeightEntry(stableWeightKg, currentTimestamp);

                lock (_stableWeightQueue)
                {
                    _stableWeightQueue.Enqueue(stableEntry);
                    if (_stableWeightQueue.Count > MaxStableWeightQueueSize)
                    {
                        _stableWeightQueue.Dequeue();
                    }
                }
                Log.Debug("稳定的重量数据已添加到队列(菜鸟): {Weight}kg, 时间: {Timestamp}. 队列大小: {QueueSize}",
                    stableEntry.WeightKg, stableEntry.Timestamp, _stableWeightQueue.Count);
            }, ex => Log.Error(ex, "后台稳定重量处理数据流订阅出错。")));

        _subscriptions.Add(_volumeCameraService.ImageStreamWithId
            .ObserveOn(TaskPoolScheduler.Default)
            .Subscribe(imageData =>
            {
                try
                {
                    imageData.Image.Freeze();
                    Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
                    {
                        try
                        {
                            CurrentImage = imageData.Image;
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "从体积相机实时流更新UI线程上的CurrentImage时出错。");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理体积相机实时流中的图像时出错。");
                }
            }, ex => Log.Error(ex, "体积相机实时图像流订阅出错。")));

        cameraDataProcessingService.PackageStream.Subscribe(async void (packageInfo) => await ProcessPackageAsync(packageInfo));

        // 在构造函数末尾，主动查询并更新相机服务的初始连接状态
        UpdateInitialDeviceStatus(_securityCameraService, "Hikvision Security");
        UpdateInitialDeviceStatus(_volumeCameraService, "Hikvision Volume");
        UpdateInitialDeviceStatus(_industrialCameraService, "Hikvision Industrial");
    }

    ~MainWindowViewModel()
    {
        Dispose(false);
    }

    public DelegateCommand OpenSettingsCommand { get; }
    public DelegateCommand OpenHistoryCommand { get; }

    public string CurrentBarcode
    {
        get => _currentBarcode;
        set => SetProperty(ref _currentBarcode, value);
    }

    public BitmapSource? CurrentImage
    {
        get => _currentImage;
        private set => SetProperty(ref _currentImage, value);
    }

    public BitmapSource? EventPackageImage
    {
        get => _eventPackageImage;
        private set => SetProperty(ref _eventPackageImage, value);
    }

    public SystemStatus SystemStatus
    {
        get => _systemStatus;
        private set => SetProperty(ref _systemStatus, value);
    }

    public ObservableCollection<PackageInfo> PackageHistory { get; } = [];
    public ObservableCollection<StatisticsItem> StatisticsItems { get; } = [];
    public ObservableCollection<DeviceStatus> DeviceStatuses { get; } = [];
    public ObservableCollection<PackageInfoItem> PackageInfoItems { get; } = [];

    private void ExecuteOpenSettings()
    {
        _dialogService.ShowDialog("SettingsDialogs", new DialogParameters(), _ => { });

    }

    private void ExecuteOpenHistory()
    {
        _dialogService.ShowDialog("PackageHistoryDialogView", new DialogParameters(), _ => { });
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        try
        {
            SystemStatus = SystemStatus.GetCurrentStatus();
            Application.Current?.Dispatcher.InvokeAsync(UpdateStatistics);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "无法更新系统状态或统计信息。");
        }
    }

    private void InitializeDeviceStatuses()
    {
        try
        {
            DeviceStatuses.Clear();
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "Hikvision Security",
                Icon = "VideoSecurity24",
            });
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "Hikvision Volume",
                Icon = "CubeScan24",
            });
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "Hikvision Industrial",
                Icon = "Camera24",
            });
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "Weight Service",
                Status = "Disconnected",
                Icon = "ScaleBalance24",
                StatusColor = "#F44336"
            });
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "Car SerialPort",
                Status = _carSortingService.IsConnected ? "Connected" : "Disconnected",
                Icon = "SerialPort24",
                StatusColor = _carSortingService.IsConnected ? "#4CAF50" : "#F44336"
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "错误初始化设备状态列表。");
        }
    }

    private void InitializeStatisticsItems()
    {
        try
        {
            StatisticsItems.Clear();

            StatisticsItems.Add(new StatisticsItem(
                label: "Total Packages",
                value: "0",
                unit: "pcs",
                description: "Total packages processed",
                icon: "BoxMultiple24"
            ));

            StatisticsItems.Add(new StatisticsItem(
                label: "Success",
                value: "0",
                unit: "pcs",
                description: "Successfully processed packages",
                icon: "CheckmarkCircle24"
            ));

            StatisticsItems.Add(new StatisticsItem(
                label: "Failed",
                value: "0",
                unit: "pcs",
                description: "Failed packages (errors, timeouts)",
                icon: "ErrorCircle24"
            ));

            StatisticsItems.Add(new StatisticsItem(
                label: "Processing Rate",
                value: "0",
                unit: "pcs/hr",
                description: "Packages processed per hour",
                icon: "ArrowTrendingLines24"
            ));

            StatisticsItems.Add(new StatisticsItem(
                label: "Peak Rate",
                value: "0",
                unit: "pcs/hr",
                description: "Highest processing rate",
                icon: "Trophy24"
            ));

        }
        catch (Exception ex)
        {
            Log.Error(ex, "错误初始化统计信息项目。");
        }
    }

    private void InitializePackageInfoItems()
    {
        try
        {
            PackageInfoItems.Clear();

            PackageInfoItems.Add(new PackageInfoItem(
                label: "Weight",
                value: "0.00",
                unit: "kg",
                description: "Package weight",
                icon: "Scales24"
            ));

            // Add Dimensions item
            PackageInfoItems.Add(new PackageInfoItem(
                label: "Dimensions",
                value: "-- x -- x --",
                unit: "mm",
                description: "L x W x H",
                icon: "ScanObject24"
            ));

            // Add Destination/Chute item
            PackageInfoItems.Add(new PackageInfoItem(
                label: "Destination",
                value: "--",
                unit: "Chute",
                description: "Assigned sorting chute",
                icon: "BranchFork24" // Example icon
            ));

            PackageInfoItems.Add(new PackageInfoItem(
                label: "Time",
                value: "--:--:--",
                description: "Processing time",
                icon: "Timer24"
            ));

            PackageInfoItems.Add(new PackageInfoItem(
                label: "Status",
                value: "Waiting",
                description: "Processing status",
                icon: "Info24"
            ));

        }
        catch (Exception ex)
        {
            Log.Error(ex, "错误初始化包裹信息项目。");
        }
    }

    private void OnSecurityCameraConnectionChanged(string? deviceId, bool isConnected)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var hikvisionStatus = DeviceStatuses.FirstOrDefault(d => d.Name == "Hikvision Security");
                if (hikvisionStatus == null) return;
                hikvisionStatus.Status = isConnected ? "Connected" : "Disconnected";
                hikvisionStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
                Log.Information("海康安防相机模块连接状态已更新: {Status}", hikvisionStatus.Status);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "在UI线程更新海康安防相机状态时出错。");
            }
        });
    }

    private void OnVolumeCameraConnectionChanged(string? deviceId, bool isConnected)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var volumeCameraStatus = DeviceStatuses.FirstOrDefault(d => d.Name == "Hikvision Volume");
                if (volumeCameraStatus == null) return;
                volumeCameraStatus.Status = isConnected ? "Connected" : "Disconnected";
                volumeCameraStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
                Log.Information("海康体积相机连接状态已更新: {Status}，设备ID: {DeviceId}", volumeCameraStatus.Status, deviceId ?? "N/A");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "在UI线程更新海康体积相机状态时出错。");
            }
        });
    }

    private void OnIndustrialCameraConnectionChanged(string? deviceId, bool isConnected)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var industrialCameraStatus = DeviceStatuses.FirstOrDefault(d => d.Name == "Hikvision Industrial");
                if (industrialCameraStatus == null) return;
                industrialCameraStatus.Status = isConnected ? "Connected" : "Disconnected";
                industrialCameraStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
                Log.Information("海康工业相机连接状态已更新: {Status}，设备ID: {DeviceId}", industrialCameraStatus.Status, deviceId ?? "N/A");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "在UI线程更新海康工业相机状态时出错。");
            }
        });
    }

    private void OnWeightServiceConnectionChanged(string? deviceId, bool isConnected)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                // 注意: DeviceStatuses 中的名称可能需要与 InitializeDeviceStatuses 中的匹配
                var weightScaleStatus = DeviceStatuses.FirstOrDefault(d => d.Name == "Weight Service");
                if (weightScaleStatus == null) return;
                weightScaleStatus.Status = isConnected ? "Connected" : "Disconnected";
                weightScaleStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
                Log.Information("称重模块连接状态已更新: {Status}", weightScaleStatus.Status);

                if (isConnected) return;
                _rawWeightBuffer.Clear();
                lock (_stableWeightQueue)
                {
                    _stableWeightQueue.Clear();
                }
                Log.Information("称重服务已断开，原始重量缓冲和稳定重量队列已清空(菜鸟)。");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "在UI线程更新称重模块状态时出错。");
            }
        });
    }

    private void OnCarSerialPortConnectionChanged(bool isConnected)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var carSerialStatus = DeviceStatuses.FirstOrDefault(d => d.Name == "Car SerialPort");
                if (carSerialStatus == null) return;
                carSerialStatus.Status = isConnected ? "Connected" : "Disconnected";
                carSerialStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
                Log.Information("小车串口连接状态变更: {Status}", carSerialStatus.Status);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "更新小车串口状态时发生错误。");
            }
        });
    }

    private async Task ProcessPackageAsync(PackageInfo package)
    {
        if (_isProcessingPackage)
        {
            Log.Information("包裹 {Barcode}: 当前正在处理另一个包裹，此包裹被忽略。", package.Barcode);
            package.ReleaseImage(); // 释放未处理包裹的图像资源
            package.Dispose();      // 释放未处理包裹的其他资源
            return;
        }

        _isProcessingPackage = true;
        var processingStopwatch = Stopwatch.StartNew(); // 记录总处理时间
        // 新增：各阶段耗时统计
        var swStage = Stopwatch.StartNew();
        long elapsedParallel = 0, elapsedUpload = 0, elapsedDest = 0, elapsedCarSort = 0;
        // 新增：并行阶段各子任务耗时
        long elapsedWeight = 0, elapsedVolume = 0, elapsedImage = 0;
        try
        {
            Log.Information("开始处理包裹: {Barcode}, 触发时间: {CreateTime:HH:mm:ss.fff}", package.Barcode, package.CreateTime);

            // 定义并行获取重量和体积的本地异步函数
            async Task<long> FetchWeightAsync(PackageInfo pkg)
            {
                var sw = Stopwatch.StartNew();
                if (pkg.Weight <= 0)
                {
                    var weightSettings = _settingsService.LoadSettings<LocalWeightSettings>();
                    var weightFetchStartTime = DateTime.Now; // Timestamp for when weight fetching starts for this package

                    // 只查找向前的稳定重量
                    var maxWaitTimeForWeightMs = weightSettings.IntegrationTimeMs > 0 ? weightSettings.IntegrationTimeMs : 500;
                    Log.Information("包裹 {Barcode}: 重量为0或无效，开始向前查找稳定重量 (最大等待 {MaxWaitMs}ms)。获取开始时间: {FetchStartTime:HH:mm:ss.fff}", pkg.Barcode, maxWaitTimeForWeightMs, weightFetchStartTime);
                    StableWeightEntry? bestStableWeight = null;
                    var swWait = Stopwatch.StartNew();
                    while (swWait.ElapsedMilliseconds < maxWaitTimeForWeightMs)
                    {
                        lock (_stableWeightQueue)
                        {
                            bestStableWeight = _stableWeightQueue
                                .Where(entry => entry.Timestamp > weightFetchStartTime)
                                .OrderBy(entry => entry.Timestamp)
                                .Cast<StableWeightEntry?>()
                                .FirstOrDefault();
                        }
                        if (bestStableWeight.HasValue)
                        {
                            pkg.Weight = bestStableWeight.Value.WeightKg;
                            Log.Information("包裹 {Barcode}: 向前查找获取到稳定重量: {Weight}kg (时间戳: {Timestamp:HH:mm:ss.fff})，耗时: {ElapsedMs}ms。", pkg.Barcode, pkg.Weight, bestStableWeight.Value.Timestamp, swWait.ElapsedMilliseconds);
                            swWait.Stop();
                            sw.Stop();
                            return sw.ElapsedMilliseconds;
                        }
                        await Task.Delay(10);
                    }
                    swWait.Stop();
                    Log.Warning("包裹 {Barcode}: 在最大等待 {MaxWaitMs}ms 内未能向前获取到稳定重量。", pkg.Barcode, maxWaitTimeForWeightMs);
                }
                else
                {
                    Log.Information("包裹 {Barcode}: 重量 {Weight}kg 有效，跳过并行重量获取。", pkg.Barcode, pkg.Weight);
                }
                sw.Stop();
                return sw.ElapsedMilliseconds;
            }

            async Task<long> FetchVolumeAsync(PackageInfo pkg)
            {
                var sw = Stopwatch.StartNew();
                if (pkg.Length.HasValue && pkg.Length.Value > 0 &&
                    pkg.Width.HasValue && pkg.Width.Value > 0 &&
                    pkg.Height.HasValue && pkg.Height.Value > 0)
                {
                    Log.Information("包裹 {Barcode}: 尺寸 长{L} 宽{W} 高{H} 已有效，跳过体积流查询。", pkg.Barcode, pkg.Length, pkg.Width, pkg.Height);
                    sw.Stop();
                    return sw.ElapsedMilliseconds;
                }

                var cameraSettings = _settingsService.LoadSettings<CameraOverallSettings>();
                int forwardWaitMs = cameraSettings.VolumeCamera.FusionTimeMs > 0 ? cameraSettings.VolumeCamera.FusionTimeMs : 500;
                var packageCreateTime = pkg.CreateTime;

                Log.Information("包裹 {Barcode}: 尺寸无效，直接从体积数据流中等待新数据。包裹创建时间: {PkgCreateTime:HH:mm:ss.fff}", pkg.Barcode, packageCreateTime);

                try
                {
                    var (Length, Width, Height, Timestamp, IsValid, Image) = await _volumeCameraService.VolumeDataWithVerticesStream
                        .Where(vd => vd.IsValid && vd.Length > 0 && vd.Width > 0 && vd.Height > 0 && vd.Timestamp > packageCreateTime)
                        .Take(1)
                        .Timeout(TimeSpan.FromMilliseconds(forwardWaitMs))
                        .FirstAsync();

                    pkg.SetDimensions(Length, Width, Height);
                    Log.Information("包裹 {Barcode}: 体积流中获取到体积: L{L:F0} W{W:F0} H{H:F0} (时间戳: {Timestamp:HH:mm:ss.fff})",
                        pkg.Barcode, Length, Width, Height, Timestamp);

                    if (Image != null)
                    {
                        Application.Current?.Dispatcher.Invoke(() => { CurrentImage = Image; });
                    }
                }
                catch (TimeoutException)
                {
                    Log.Warning("包裹 {Barcode}: 在{WaitMs}ms内体积流未推送到新体积数据。", pkg.Barcode, forwardWaitMs);
                }
                sw.Stop();
                return sw.ElapsedMilliseconds;
            }

            async Task<(string? imageUrl, long captureMs, long uploadMs)> FetchSecurityImageAndUploadAsync(PackageInfo package)
            {
                var swCapture = Stopwatch.StartNew();
                // 中文注释：安防相机抓图
                var capturedImage = _securityCameraService.CaptureAndGetBitmapSource();
                swCapture.Stop();
                long elapsedCapture = swCapture.ElapsedMilliseconds;
                string? imageUrl = null;
                long elapsedUpload = 0;
                if (capturedImage != null)
                {
                    string tempImageDirectory = Path.Combine(Path.GetTempPath(), "RookieParcelImages");
                    string tempImageFileName = $"pkg_{package.Index}_{DateTime.UtcNow:yyyyMMddHHmmssfff}.jpg";
                    string? tempImagePath = SaveBitmapSourceAsJpeg(capturedImage, tempImageDirectory, tempImageFileName);
                    if (tempImagePath != null)
                    {
                        Log.Information("包裹 {Barcode}: 安防相机图片已保存到临时路径 {Path}", package.Barcode, tempImagePath);
                        var swUpload = Stopwatch.StartNew();
                        imageUrl = await _rookieApiService.UploadImageAsync(tempImagePath);
                        swUpload.Stop();
                        elapsedUpload = swUpload.ElapsedMilliseconds;
                        // 不再删除本地图片
                        if (!string.IsNullOrEmpty(imageUrl))
                        {
                            Log.Information("包裹 {Barcode}: 图片上传到DCS成功: {ImageUrl}", package.Barcode, imageUrl);
                        }
                        else
                        {
                            Log.Warning("包裹 {Barcode}: 图片上传到DCS失败。", package.Barcode);
                        }
                    }
                    else
                    {
                        Log.Warning("包裹 {Barcode}: 保存安防相机抓图到临时文件失败。", package.Barcode);
                    }
                }
                else
                {
                    Log.Information("包裹 {Barcode}: 未从安防相机抓取到图片，跳过图片上传。", package.Barcode);
                }
                Log.Information("包裹 {Barcode}: 安防相机抓图耗时: {CaptureMs}ms，上传OSS耗时: {UploadMs}ms", package.Barcode, elapsedCapture, elapsedUpload);
                return (imageUrl, elapsedCapture, elapsedUpload);
            }

            // 并行获取重量、体积、安防相机图片上传
            Log.Information("包裹 {Barcode}: 开始并行获取重量、体积和安防相机图片上传。", package.Barcode);
            swStage.Restart();
            var fetchWeightTask = FetchWeightAsync(package);
            var fetchVolumeTask = FetchVolumeAsync(package);
            var fetchSecurityImageTask = FetchSecurityImageAndUploadAsync(package);
            await Task.WhenAll(fetchWeightTask, fetchVolumeTask, fetchSecurityImageTask);
            elapsedParallel = swStage.ElapsedMilliseconds;
            elapsedWeight = fetchWeightTask.Result;
            elapsedVolume = fetchVolumeTask.Result;
            elapsedImage = fetchSecurityImageTask.Result.captureMs + fetchSecurityImageTask.Result.uploadMs;
            package.ImagePath = fetchSecurityImageTask.Result.imageUrl; // 并行任务结果赋值
            Log.Information("包裹 {Barcode}: 重量、体积和安防相机图片并行获取阶段完成。重量: {Weight}kg, 长宽高: {L}x{W}x{H}mm, 图片路径: {ImagePath}",
                package.Barcode,
                package.Weight,
                package.Length.HasValue ? package.Length.Value.ToString("F0") : "N/A",
                package.Width.HasValue ? package.Width.Value.ToString("F0") : "N/A",
                package.Height.HasValue ? package.Height.Value.ToString("F0") : "N/A",
                package.ImagePath ?? "无");
            Log.Information("包裹 {Barcode}: 并行阶段（重量、体积、图片）总耗时: {Elapsed}ms", package.Barcode, elapsedParallel);
            Log.Information("包裹 {Barcode}: 重量获取耗时: {WeightMs}ms, 体积获取耗时: {VolumeMs}ms, 安防相机抓图耗时: {CaptureMs}ms, 上传OSS耗时: {UploadMs}ms", package.Barcode, elapsedWeight, elapsedVolume, fetchSecurityImageTask.Result.captureMs, fetchSecurityImageTask.Result.uploadMs);
            var maxElapsed = Math.Max(elapsedWeight, Math.Max(elapsedVolume, elapsedImage));
            string slowest = (maxElapsed == elapsedWeight) ? "重量获取" :
                             (maxElapsed == elapsedVolume) ? "体积获取" : (maxElapsed == fetchSecurityImageTask.Result.captureMs ? "安防相机抓图" : "图片上传");
            Log.Information("包裹 {Barcode}: 并行阶段最慢环节为: {Slowest}，耗时: {Elapsed}ms", package.Barcode, slowest, maxElapsed);

            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                // CurrentImage = package.VolumeImage; // 不再需要
                package.SetIndex((int)Interlocked.Increment(ref _globalPackageIndex));
                CurrentBarcode = package.Barcode;
                UpdatePackageInfoItems(package);
                Interlocked.Increment(ref _totalPackageCount);
            });

            // 检查重量和尺寸是否有效，无效则直接失败
            bool hasWeight = package.Weight > 0;
            bool hasDimensions = package is { Length: > 0, Width: > 0, Height: > 0 };

            string dcsErrorMessage = string.Empty; // 用于存储DCS交互过程中的错误信息
            bool isException = false;

            if (!hasWeight || !hasDimensions)
            {
                string uiErrorMessage = hasWeight switch
                {
                    false when !hasDimensions => "Package weight and volume information are missing",
                    false => "Package weight information is missing",
                    _ => "Package volume information is missing"
                };
                string logMessage = hasWeight switch
                {
                    false when !hasDimensions => "包裹重量和体积信息均缺失",
                    false => "包裹重量信息缺失",
                    _ => "包裹体积信息缺失"
                };

                Log.Warning("包裹 {Barcode}: {Reason}，处理终止。", package.Barcode, logMessage);
                package.SetStatus(PackageStatus.Failed, uiErrorMessage); // UI相关的错误信息使用英文

                // 分配异常口
                if (_carSequenceSettings.ExceptionChuteNumber > 0)
                {
                    package.SetChute(_carSequenceSettings.ExceptionChuteNumber);
                    Log.Information("包裹 {Barcode}: 因重量或体积无效，已分配异常口: {ExceptionChute}", package.Barcode, _carSequenceSettings.ExceptionChuteNumber);
                }
                else
                {
                    Log.Warning("包裹 {Barcode}: 未配置异常口，无法分配异常口。", package.Barcode);
                }
                isException = true;
            }
            else // 重量和尺寸有效，开始DCS交互和小车分拣流程
            {
                // 1. DCS相关操作：上传包裹信息，获取目的地建议
                // 这些操作的失败不应阻止后续的小车分拣（如果适用），但会影响最终状态和上报信息

                // 1.1 调用 sorter.parcel_info_upload
                Log.Information("包裹 {Barcode}: 准备上传包裹基础信息到DCS。图片路径: {ImagePath}", package.Barcode, package.ImagePath ?? "无");
                swStage.Restart();
                bool parcelInfoUploaded = await _rookieApiService.UploadParcelInfoAsync(package);
                elapsedUpload = swStage.ElapsedMilliseconds;
                Log.Information("包裹 {Barcode}: 上传包裹信息接口耗时: {Elapsed}ms", package.Barcode, elapsedUpload);
                if (!parcelInfoUploaded)
                {
                    dcsErrorMessage = AppendError(dcsErrorMessage, "Parcel info upload to DCS failed.");
                    Log.Error("包裹 {Barcode}: 包裹基础信息上传DCS失败。 {DcsError}", package.Barcode, dcsErrorMessage);
                }
                else // 包裹基础信息上传DCS成功
                {
                    Log.Information("包裹 {Barcode}: 包裹基础信息上传DCS成功。", package.Barcode);
                    // 1.2 调用 sorter.dest_request (仅当 parcel_info_upload 成功)
                    swStage.Restart();
                    var destResult = await _rookieApiService.RequestDestinationAsync(package.Barcode);
                    elapsedDest = swStage.ElapsedMilliseconds;
                    Log.Information("包裹 {Barcode}: 请求目的地接口耗时: {Elapsed}ms", package.Barcode, elapsedDest);
                    bool dcsDestinationSuccess = destResult?.ErrorCode == 0 && int.TryParse(destResult.ChuteCode, out int apiChute) && apiChute > 0;

                    if (dcsDestinationSuccess)
                    {
                        package.SetChute(_nextCyclicChute); // ChuteNumber is now set by cyclic logic
                        Log.Information("包裹 {Barcode}: DCS请求目的地成功。已覆盖API分配格口，循环分配格口为: {CyclicChute}", package.Barcode, _nextCyclicChute);
                    }
                    else // DCS请求目的地失败或返回无效格口
                    {
                        string destFailReason = destResult == null
                            ? "DCS destination request API call failed"
                            : $"DCS destination request logical error: Code {destResult.ErrorCode}, Chute '{destResult.ChuteCode}'";
                        dcsErrorMessage = AppendError(dcsErrorMessage, destFailReason);
                        Log.Warning("包裹 {Barcode}: DCS 请求目的地失败/无效。原因: {Reason}", package.Barcode, destFailReason);
                        // package.ChuteNumber 此时未被DCS设置或保持原样(可能为0)
                    }
                }
                // --- DCS交互结束 --- 

                // 2. 确定小车分拣的目标格口 (独立于DCS是否成功，但可受其影响)
                if (package.ChuteNumber <= 0) // 如果DCS未能成功设置有效格口 (包括 parcelInfoUpload 失败的情况)
                {
                    if (_carSequenceSettings.ExceptionChuteNumber > 0)
                    {
                        package.SetChute(_carSequenceSettings.ExceptionChuteNumber);
                        Log.Information("包裹 {Barcode}: DCS未成功分配格口或交互失败，已分配配置的异常格口: {ExceptionChute} 用于小车分拣。", package.Barcode, _carSequenceSettings.ExceptionChuteNumber);
                    }
                    else
                    {
                        // package.ChuteNumber 保持为0或无效。小车服务需能处理。
                        Log.Warning("包裹 {Barcode}: DCS未成功分配格口或交互失败，且未配置有效异常格口。小车分拣将使用当前格口: {Chute}", package.Barcode, package.ChuteNumber);
                    }
                }
                // DCS请求目的地成功，使用循环格口覆盖API返回的格口
                if (_nextCyclicChute is <= 0 or >= 4) // 如果是初始值或达到上限，则重置为1开始
                {
                    _nextCyclicChute = 1;
                }
                else
                {
                    _nextCyclicChute++;
                }
                package.SetChute(_nextCyclicChute);
            }

            // 无论是否异常，都调用小车分拣服务
            Log.Information("包裹 {Barcode}: 准备调用小车分拣服务。目标格口: {Chute}", package.Barcode, package.ChuteNumber > 0 ? package.ChuteNumber.ToString() : "未指定/异常");
            swStage.Restart();
            var carSortResult = await _carSortService.ProcessPackageSortingAsync(package); // 应该能够处理 package.ChuteNumber <= 0 的情况
            elapsedCarSort = swStage.ElapsedMilliseconds;
            Log.Information("包裹 {Barcode}: 小车分拣服务耗时: {Elapsed}ms", package.Barcode, elapsedCarSort);

            string? carSortErrorMessage = null;
            if (!carSortResult)
            {
                carSortErrorMessage = "Car sorting failed."; // UI 英文
                Log.Warning("包裹 {Barcode}: 小车分拣服务处理失败。", package.Barcode);
            }
            else
            {
                Log.Information("包裹 {Barcode}: 小车分拣服务处理成功。", package.Barcode);
            }

            // 4. 根据小车分拣结果和DCS交互历史，设定最终状态和错误信息
            string finalCombinedErrorMessage = dcsErrorMessage;
            if (carSortErrorMessage != null)
            {
                finalCombinedErrorMessage = AppendError(finalCombinedErrorMessage, carSortErrorMessage);
            }

            // 小车分拣失败
            // 即使DCS有错，但小车分拣成功了，整体认为是成功的，但错误信息会保留DCS的问题
            if (isException)
            {
                package.SetStatus(carSortResult ? PackageStatus.Failed : PackageStatus.Error, finalCombinedErrorMessage);
            }
            else
            {
                package.SetStatus(carSortResult ? PackageStatus.Success : PackageStatus.Error, finalCombinedErrorMessage);
            }

            // 5. 上报分拣结果到DCS (无论之前发生什么，都尝试上报)
            string chuteToReport;
            if (package.ChuteNumber > 0) // 使用最终确定的 package.ChuteNumber
            {
                chuteToReport = package.ChuteNumber.ToString();
            }
            else // 如果 package.ChuteNumber 最终为0 (例如DCS失败且异常格口也为0)
            {
                chuteToReport = _carSequenceSettings.ExceptionChuteNumber > 0
                                ? _carSequenceSettings.ExceptionChuteNumber.ToString()
                                : "N/A"; // 如果配置的异常格口也无效，则上报N/A
                Log.Information("包裹 {Barcode}: 最终指定格口为0或无效，将尝试使用配置的异常口 ({ConfiguredExceptionChute}) 或 N/A ({ChuteToReportActual}) 上报DCS。",
                    package.Barcode,
                    _carSequenceSettings.ExceptionChuteNumber,
                    chuteToReport);
            }

            // 根据最终的 package.Status 判断上报给API的成功状态
            bool successForApiReport = package.Status == PackageStatus.Success;
            string? errorReasonForApiReportUi = successForApiReport
                ? null
                : (string.IsNullOrEmpty(package.ErrorMessage) ? "Unknown sorting error" : package.ErrorMessage); // ErrorMessage已经处理为UI适用的英文
            string? errorReasonForApiReportLog = successForApiReport // 中文日志
                ? null
                : (string.IsNullOrEmpty(package.ErrorMessage) ? "未知分拣错误" : package.ErrorMessage); // ErrorMessage可能是英文，但作日志用途也可

            Log.Information("包裹 {Barcode}: 准备上报分拣结果到DCS。格口: {Chute}, 状态: {IsSuccess}, 原因(日志): {ReasonLog}",
                package.Barcode, chuteToReport, successForApiReport, errorReasonForApiReportLog);

            bool reportAcknowledged = await _rookieApiService.ReportSortResultAsync(package.Barcode, chuteToReport, successForApiReport, errorReasonForApiReportUi); // 上报给API的错误原因使用UI版
            if (!reportAcknowledged)
            {
                Log.Warning("包裹 {Barcode}: 上报分拣结果到DCS失败。最终状态: {Status}, 格口: {Chute}",
                    package.Barcode, package.StatusDisplay, chuteToReport); // StatusDisplay 假设由 PackageInfo 控制，可能是中文或已本地化
            }
            else
            {
                Log.Information("包裹 {Barcode}: 分拣结果已成功上报到DCS。", package.Barcode);
            }

            // 统一的UI更新和历史记录
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                UpdatePackageInfoItems(package);
                if (package.Status == PackageStatus.Success)
                    Interlocked.Increment(ref _successPackageCount);
                else
                    Interlocked.Increment(ref _failedPackageCount); // 包括 Error 和 Failed 状态
                UpdateStatistics();
                PackageHistory.Insert(0, package);
                if (PackageHistory.Count > 1000)
                    PackageHistory.RemoveAt(PackageHistory.Count - 1);
                Log.Information("包裹 {Barcode} 处理流程核心逻辑结束, 最终状态: {Status}, 最终错误(用于日志): {ErrorMessageLog}", package.Barcode, package.Status, package.ErrorMessage ?? "无");
            });
            package.ReleaseImage(); // 释放工业相机图像（如果存在）
        }
        catch (Exception ex) // 添加一个通用的try-catch来记录未预料的异常
        {
            Log.Error(ex, "处理包裹 {Barcode} 时发生意外错误。", package.Barcode);
            if (package.Status != PackageStatus.Failed && package.Status != PackageStatus.Error)
            {
                // UI 英文. Ensure ErrorMessage on package is also set to this if it's directly bound to UI.
                package.SetStatus(PackageStatus.Error, $"Unexpected error during processing: {ex.Message}");
            }
        }
        finally
        {
            processingStopwatch.Stop();
            package.ProcessingTime = (int)processingStopwatch.ElapsedMilliseconds;
            _isProcessingPackage = false;
            // StatusDisplay 假设由 PackageInfo 控制，可能是中文或已本地化
            Log.Information("包裹 {Barcode} 处理流程结束 (或者被忽略后标志位重置)。最终状态: {StatusDisplay}, 总耗时: {TotalTime}ms", package.Barcode, package.StatusDisplay, package.ProcessingTime);
        }
    }

    private void UpdatePackageInfoItems(PackageInfo package)
    {
        var weightItem = PackageInfoItems.FirstOrDefault(i => i.Label == "Scale");
        if (weightItem != null) { weightItem.Value = package.Weight.ToString("F2"); }

        // Update Dimensions item
        var dimensionsItem = PackageInfoItems.FirstOrDefault(i => i.Label == "Dimensions");
        if (dimensionsItem != null)
        {
            if (package.Length.HasValue && package.Length.Value > 0 &&
                package.Width.HasValue && package.Width.Value > 0 &&
                package.Height.HasValue && package.Height.Value > 0)
            {
                dimensionsItem.Value = $"{package.Length.Value:F0}x{package.Width.Value:F0}x{package.Height.Value:F0}";
            }
            else
            {
                dimensionsItem.Value = "-- x -- x --";
            }
        }

        // Update Destination/Chute item
        var destinationItem = PackageInfoItems.FirstOrDefault(i => i.Label == "Destination");
        if (destinationItem != null)
        {
            if (package.ChuteNumber > 0)
            {
                destinationItem.Value = package.ChuteNumber.ToString();
            }
            else if (package.Status == PackageStatus.Error || package.Status == PackageStatus.Failed || !string.IsNullOrEmpty(package.ErrorMessage))
            {
                destinationItem.Value = "ERR"; // Or some other indicator for error/no chute
            }
            else
            {
                destinationItem.Value = "--";
            }
        }

        var timeItem = PackageInfoItems.FirstOrDefault(i => i.Label == "Time");
        if (timeItem != null) { timeItem.Value = package.CreateTime.ToString("HH:mm:ss"); }

        var statusItem = PackageInfoItems.FirstOrDefault(i => i.Label == "Status");
        if (statusItem == null) return;
        statusItem.Value = package.StatusDisplay;
        statusItem.Description = package.ErrorMessage ?? package.StatusDisplay;
        statusItem.StatusColor = package.Status switch
        {
            PackageStatus.Success => "#4CAF50",
            PackageStatus.Error => "#F44336",
            PackageStatus.Timeout => "#FF9800",
            PackageStatus.NoRead => "#FFEB3B",
            _ => "#2196F3"
        };
    }

    private void UpdateStatistics()
    {
        var totalItem = StatisticsItems.FirstOrDefault(i => i.Label == "Total Packages");
        if (totalItem != null) totalItem.Value = _totalPackageCount.ToString();

        var successItem = StatisticsItems.FirstOrDefault(i => i.Label == "Success");
        if (successItem != null) successItem.Value = _successPackageCount.ToString();

        var failedItem = StatisticsItems.FirstOrDefault(i => i.Label == "Failed");
        if (failedItem != null) failedItem.Value = _failedPackageCount.ToString(); // Failed 包括了 Error 和 Failed 状态

        var rateItem = StatisticsItems.FirstOrDefault(i => i.Label == "Processing Rate");
        if (rateItem == null) return;
        {
            var minuteAgo = DateTime.Now.AddMinutes(-1);
            var lastMinuteCount = PackageHistory.Count(p => p.CreateTime > minuteAgo);
            var hourlyRate = lastMinuteCount * 60;
            rateItem.Value = hourlyRate.ToString();

            if (hourlyRate <= _peakRate) return;
            _peakRate = hourlyRate;
            var peakRateItem = StatisticsItems.FirstOrDefault(i => i.Label == "Peak Rate");
            if (peakRateItem != null) peakRateItem.Value = _peakRate.ToString();
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            try
            {
                _timer.Stop();
                _timer.Tick -= Timer_Tick;
                _securityCameraService.ConnectionChanged -= OnSecurityCameraConnectionChanged;
                _volumeCameraService.ConnectionChanged -= OnVolumeCameraConnectionChanged;
                _industrialCameraService.ConnectionChanged -= OnIndustrialCameraConnectionChanged;
                _weightService.ConnectionChanged -= OnWeightServiceConnectionChanged;
                _carSortingService.ConnectionChanged -= OnCarSerialPortConnectionChanged;

                foreach (var subscription in _subscriptions)
                {
                    subscription.Dispose();
                }
                _subscriptions.Clear();

                Application.Current?.Dispatcher.Invoke(() =>
                {
                    PackageHistory.Clear();
                    StatisticsItems.Clear();
                    DeviceStatuses.Clear();
                    PackageInfoItems.Clear();
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "MainWindowViewModel 释放期间发生错误。");
            }
        }

        _disposed = true;
    }

    // 辅助方法用于拼接错误信息，避免前导分号
    private static string AppendError(string? existingError, string newError)
    {
        if (string.IsNullOrEmpty(existingError))
        {
            return newError;
        }
        return $"{existingError}; {newError}";
    }

    /// <summary>
    /// 将 BitmapSource 保存为 JPEG 文件。
    /// </summary>
    /// <param name="bitmapSource">要保存的 BitmapSource。</param>
    /// <param name="directory">目标目录。</param>
    /// <param name="fileName">目标文件名 (含扩展名)。</param>
    /// <returns>成功则返回完整文件路径，否则返回 null。</returns>
    private static string? SaveBitmapSourceAsJpeg(BitmapSource? bitmapSource, string directory, string fileName)
    {
        if (bitmapSource == null) return null;

        string filePath = Path.Combine(directory, fileName);
        try
        {
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var fileStream = new FileStream(filePath, FileMode.Create);
            BitmapEncoder encoder = new JpegBitmapEncoder
            {
                QualityLevel = 85 // 可选: 设置JPEG质量
            };
            encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
            encoder.Save(fileStream);

            Log.Debug("BitmapSource 已成功保存为 JPEG: {FilePath}", filePath);
            return filePath;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存 BitmapSource 到文件 {FilePath} 失败。", filePath);
            return null;
        }
    }

    // 新增辅助方法，用于主动更新设备状态
    private void UpdateInitialDeviceStatus(ICameraService cameraService, string deviceName)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var deviceStatus = DeviceStatuses.FirstOrDefault(d => d.Name == deviceName);
                if (deviceStatus == null)
                {
                    Log.Warning("在 DeviceStatuses 中未找到名为 {DeviceName} 的设备项。", deviceName);
                    return;
                }

                bool isConnected = cameraService.IsConnected;
                deviceStatus.Status = isConnected ? "Connected" : "Disconnected";
                deviceStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
                Log.Information("主动更新设备初始状态: {DeviceName} - {Status}", deviceName, deviceStatus.Status);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "在UI线程主动更新设备 {DeviceName} 初始状态时出错。", deviceName);
            }
        });
    }
}