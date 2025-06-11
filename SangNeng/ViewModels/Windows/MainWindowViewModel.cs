using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Camera.Interface;
using Camera.Services.Implementations.RenJia;
using Common.Models.Package;
using Common.Services.Audio;
using Common.Services.Settings;
using Common.Services.Ui;
using History.Data;
using History.Views.Dialogs;
using Serilog;
using Sunnen.Events;
using Sunnen.Models;
using Sunnen.Services;
using Sunnen.ViewModels.Settings;
using Color = System.Drawing.Color;
using Timer = System.Timers.Timer;
using System.Diagnostics;
using Camera.Models.Settings;
using Common.Models;
using DimensionImageSaveMode = Camera.Models.Settings.DimensionImageSaveMode;
using History.Configuration;
using Weight.Services;
using System.Text;

namespace Sunnen.ViewModels.Windows;

/// <summary>
/// 主窗口视图模型
/// </summary>
public class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly IAudioService _audioService = null!;
    private readonly ICameraService _cameraService = null!;
    private readonly IDialogService _dialogService = null!;
    private readonly SemaphoreSlim _measurementLock = new(1, 1);
    private readonly IPackageHistoryDataService _packageHistoryDataService = null!;
    private readonly ISangNengService _sangNengService = null!;
    private readonly ISettingsService _settingsService = null!;
    private readonly Timer _timer = null!;
    private readonly RenJiaCameraService _volumeCamera = null!;
    private readonly IWeightService _weightService = null!;
    private readonly INotificationService _notificationService = null!;
    private ObservableCollection<SelectablePalletModel> _availablePallets = null!;
    private string _currentBarcode = string.Empty;
    private ImageSource? _currentImage;
    private PackageInfo _currentPackage = null!;
    private ObservableCollection<DeviceStatus> _deviceStatuses = [];
    private bool _disposed;
    private ObservableCollection<PackageInfo> _packageHistory = [];
    private ObservableCollection<PackageInfoItem> _packageInfoItems = [];
    private SelectablePalletModel? _selectedPallet;
    private ObservableCollection<StatisticsItem> _statisticsItems = [];
    private SystemStatus _systemStatus = SystemStatus.GetCurrentStatus();
    private ImageSource? _volumeImage;
    private readonly SemaphoreSlim _barcodeProcessingLock = new(1, 1);

    // 新增：MVVM模式的条码输入处理相关字段和事件
    private bool _isScanningInProgress;
    private readonly StringBuilder _barcodeBuffer = new();

    // 请求UI操作的事件
    public event Action? RequestClearBarcodeInput;
    public event Action? RequestFocusBarcodeInput;
    public event Action? RequestFocusToWindow; // 新增：请求将焦点移到主窗口

    // 新增：用于稳定重量处理的字段
    private readonly struct StableWeightEntry(double weightKg, DateTime timestamp)
    {
        public double WeightKg { get; } = weightKg;
        public DateTime Timestamp { get; } = timestamp;
    }

    private readonly Queue<StableWeightEntry> _stableWeightQueue = new();
    private const int MaxStableWeightQueueSize = 100;
    private readonly List<double> _rawWeightBuffer = [];

    // 新增：用于存储Rx订阅的字段
    private readonly IDisposable? _weightDataSubscription;
    private readonly IDisposable? _volumeCameraImageSubscription;
    private readonly IDisposable? _cameraServiceImageSubscription;
    private readonly IDisposable? _palletSettingsChangedSubscription;

    private bool _isInitializingOverlayVisible = true; // 新增：控制初始化遮罩的可见性

    /// <summary>
    /// 构造函数
    /// </summary>
    public MainWindowViewModel(
        IDialogService dialogService,
        RenJiaCameraService volumeCamera,
        ICameraService cameraService,
        IWeightService weightService,
        ISettingsService settingsService,
        IPackageHistoryDataService packageHistoryDataService,
        IAudioService audioService,
        IEventAggregator eventAggregator,
        ISangNengService sangNengService,
        INotificationService notificationService)
    {
        _dialogService = dialogService;
        _volumeCamera = volumeCamera;
        _cameraService = cameraService;
        _weightService = weightService;
        _settingsService = settingsService;
        _packageHistoryDataService = packageHistoryDataService;
        _audioService = audioService;
        _sangNengService = sangNengService;
        _notificationService = notificationService;

        OpenSettingsCommand = new DelegateCommand(ExecuteOpenSettings);
        OpenHistoryWindowCommand = new DelegateCommand(() =>
        {
            var dialogParams = new DialogParameters
            {
                { "customViewConfiguration", CreateSangNengHistoryViewConfiguration() }
            };
            _dialogService.ShowDialog(nameof(PackageHistoryDialogView), dialogParams);
        });
        InitializePackageInfoItems();
        InitializeStatisticsItems();
        InitializeDeviceStatuses();

        // 订阅体积相机连接状态
        _volumeCamera.ConnectionChanged += OnVolumeCameraConnectionChanged;

        // 订阅拍照相机连接状态
        _cameraService.ConnectionChanged += OnCameraConnectionChanged;

        // 订阅重量称连接状态
        _weightService.ConnectionChanged += OnWeightScaleConnectionChanged;

        // 订阅重量数据流
        _weightDataSubscription = _weightService.WeightDataStream.Subscribe(streamTuple =>
        {
            // 在事件处理中按需加载配置
            var weightSettings = _settingsService.LoadSettings<Weight.Models.Settings.WeightSettings>();
            var stabilityCheckSamples =
                weightSettings.StabilityCheckSamples > 0 ? weightSettings.StabilityCheckSamples : 5;
            var stabilityThresholdGrams = weightSettings.StabilityThresholdGrams > 0
                ? weightSettings.StabilityThresholdGrams
                : 100;
            // 将阈值从克转换为千克，以便与缓冲区中的千克值比较
            var stabilityThresholdKg = stabilityThresholdGrams / 1000.0;

            if (streamTuple == null) // 添加对 streamTuple 为 null 的检查
            {
                Log.Warning("从 WeightDataStream 接收到 null 数据元组。");
                return;
            }

            // streamTuple.Value 已经是 kg
            double currentRawWeightKg = streamTuple.Value;
            var currentTimestamp = streamTuple.Timestamp;

            // 将当前原始重量(kg)添加到缓冲区，忽略0值
            if (currentRawWeightKg > 0)
            {
                _rawWeightBuffer.Add(currentRawWeightKg);
                if (_rawWeightBuffer.Count > stabilityCheckSamples)
                {
                    _rawWeightBuffer.RemoveAt(0); // 保持缓冲区大小固定（滑动窗口）
                }
                Log.Debug("重量数据已添加到缓冲区: {Weight}kg, 缓冲区大小: {BufferSize}/{RequiredSize}", 
                    currentRawWeightKg, _rawWeightBuffer.Count, stabilityCheckSamples);
            }
            else
            {
                Log.Debug("忽略零重量或负重量: {Weight}kg", currentRawWeightKg);
            }
            
            // 如果缓冲区已满，则检查稳定性
            if (_rawWeightBuffer.Count != stabilityCheckSamples) return;
            
            // 在稳定性检查时计算当前窗口的最小值和最大值
            var minWeightInWindow = _rawWeightBuffer.Min();
            var maxWeightInWindow = _rawWeightBuffer.Max();
            var weightDifference = maxWeightInWindow - minWeightInWindow;

            Log.Debug("稳定性检查: 最小值={Min}kg, 最大值={Max}kg, 差值={Diff}kg, 阈值={Threshold}kg", 
                minWeightInWindow, maxWeightInWindow, weightDifference, stabilityThresholdKg);

            // 使用转换后的 stabilityThresholdKg 进行比较
            if (!(weightDifference < stabilityThresholdKg)) 
            {
                Log.Debug("重量不稳定，差值 {Diff}kg 超过阈值 {Threshold}kg", weightDifference, stabilityThresholdKg);
                return;
            }
            // 数据稳定
            // _rawWeightBuffer 中的数据已经是 kg，所以 Average() 也是 kg
            var stableAverageWeightKg = _rawWeightBuffer.Average();
            // 使用稳定窗口中最后一个样本的时间戳
            // stableAverageWeightKg 已经是 kg，直接使用
            var stableEntry = new StableWeightEntry(stableAverageWeightKg, currentTimestamp);

            lock (_stableWeightQueue) // 确保队列操作的线程安全
            {
                _stableWeightQueue.Enqueue(stableEntry);
                if (_stableWeightQueue.Count > MaxStableWeightQueueSize)
                {
                    _stableWeightQueue.Dequeue();
                }
                Log.Information("稳定重量已添加到队列: {Weight}kg, 时间戳: {Timestamp:HH:mm:ss.fff}, 队列大小: {QueueSize}", 
                    stableAverageWeightKg, currentTimestamp, _stableWeightQueue.Count);
            }
        });

        // 启动系统状态更新定时器
        _timer = new Timer(1000);
        _timer.Elapsed += (_, _) => { SystemStatus = SystemStatus.GetCurrentStatus(); };
        _timer.Start();

        // 订阅体积相机图像流
        _volumeCameraImageSubscription = _volumeCamera.ImageStream
            .Subscribe(imageData =>
            {
                try
                {
                    VolumeImage = imageData;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理体积相机图像流数据时发生错误");
                }
            });

        // 修改为使用 ImageStreamWithId
        _cameraServiceImageSubscription = _cameraService.ImageStreamWithId
            .Select(data => data.Image) // 从元组中选择图像
            .Subscribe(imageData =>
            {
                try
                {
                    // 直接赋值给CurrentImage
                    CurrentImage = imageData;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理拍照相机图像流数据时发生错误");
                }
            });

        _availablePallets = [];

        SelectPalletCommand = new DelegateCommand<SelectablePalletModel>(ExecuteSelectPallet);

        // 新增：初始化条码输入处理命令
        HandleScanStartCommand = new DelegateCommand(ExecuteHandleScanStart);
        HandleBarcodeCompleteCommand = new DelegateCommand(ExecuteHandleBarcodeComplete);

        // 订阅托盘设置更改事件
        _palletSettingsChangedSubscription =
            eventAggregator.GetEvent<PalletSettingsChangedEvent>().Subscribe(LoadAvailablePallets);

        // 加载可用托盘
        LoadAvailablePallets();
        UpdateInitialDeviceStatuses();
    }

    public MainWindowViewModel(IDisposable? volumeCameraImageSubscription)
    {
        _volumeCameraImageSubscription = volumeCameraImageSubscription;
    }

    public void Dispose()
    {
        if (_disposed) return;

        try
        {
            // 取消Rx订阅
            _weightDataSubscription?.Dispose();
            _volumeCameraImageSubscription?.Dispose();
            _cameraServiceImageSubscription?.Dispose();
            _palletSettingsChangedSubscription?.Dispose();

            // 停止定时器
            _timer.Stop();
            _timer.Dispose();

            // 取消事件订阅
            _volumeCamera.ConnectionChanged -= OnVolumeCameraConnectionChanged;
            _cameraService.ConnectionChanged -= OnCameraConnectionChanged;
            _weightService.ConnectionChanged -= OnWeightScaleConnectionChanged;

            // 释放信号量
            _measurementLock.Dispose();
            _barcodeProcessingLock.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "释放资源时发生错误");
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void UpdateStatistics()
    {
        var items = StatisticsItems.ToList();

        // 更新总数
        items[0].Value = PackageHistory.Count.ToString();

        // 更新成功数
        var successCount = PackageHistory.Count(p => p.Status == "Complete");
        items[1].Value = successCount.ToString();

        // 更新失败数
        var failedCount = PackageHistory.Count(p => p.Status == "Failed");
        items[2].Value = failedCount.ToString();

        // 计算处理速率（每小时）
        if (PackageHistory.Any())
        {
            var timeSpan = DateTime.Now - PackageHistory.Min(p => p.CreateTime);
            var ratePerHour = PackageHistory.Count / timeSpan.TotalHours;
            items[3].Value = $"{ratePerHour:F0}";
        }

        StatisticsItems = [.. items];
    }

    // 将原有的实现移到这个方法中，并设为 internal 以便视图调用
    internal async Task ProcessBarcodeAsync(string barcode)
    {
        // 尝试获取处理锁，如果已被占用则直接返回
        if (!await _barcodeProcessingLock.WaitAsync(0)) // 设置超时为0，如果锁不可用则立即返回false
        {
            Log.Warning("处理单元繁忙，忽略条码: {Barcode}", barcode);
            return;
        }

        try
        {
            // 播放离开提示音（leave.wav）
            _ = _audioService.PlayPresetAsync(AudioType.Leave);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                InitializePackageInfoItems();
                CurrentBarcode = barcode;
                Log.Information("UI已更新条码: {Barcode}", barcode);
                var items = PackageInfoItems.ToList();
                if (items.Count <= 2) return;
                items[2].Value = DateTime.Now.ToString("HH:mm:ss");
                PackageInfoItems = [.. items];
            });

            // 加载体积相机配置
            var cameraOverallSettings = _settingsService.LoadSettings<CameraOverallSettings>();
            Log.Information("等待处理包裹，延迟: {TimeoutMs}ms", cameraOverallSettings.VolumeCamera.FusionTimeMs);

            var fusionMs = cameraOverallSettings.VolumeCamera.FusionTimeMs;
            // 等待延迟
            await Task.Delay(fusionMs);

            // 等待阶段已过，进入实际处理阶段，不再响应新条码
            // 后续原有处理逻辑保持不变

            Log.Information("延迟结束，开始处理包裹: {Barcode}",
                barcode); // 使用此实例的正确条码进行日志记录

            try // 内部 try 块，处理包裹逻辑并捕获特定异常
            {
                Log.Information("开始创建包裹对象: {Barcode}", barcode);

                // 确定统一的处理时间戳，在整个处理过程中使用
                var processingTimestamp = DateTime.Now;

                // 创建新的包裹对象
                _currentPackage = PackageInfo.Create();
                _currentPackage.SetBarcode(barcode); // 使用一致的条码
                _currentPackage.CreateTime = processingTimestamp; // 设置统一的处理时间

                // 设置当前选中的托盘信息到包裹实例
                if (SelectedPallet != null)
                {
                    _currentPackage.SetPallet(
                        SelectedPallet.Name,
                        SelectedPallet.Weight,
                        SelectedPallet.Length,
                        SelectedPallet.Width,
                        SelectedPallet.Height);

                    Log.Information("已设置包裹托盘信息: {PalletName}, 重量: {Weight}kg, 尺寸: {Length}×{Width}×{Height}cm",
                        SelectedPallet.Name, SelectedPallet.Weight,
                        SelectedPallet.Length, SelectedPallet.Width, SelectedPallet.Height);
                }

                try
                {
                    // 串行执行：先拍照，再称重，最后体积测量
                    BitmapSource? capturedImage = null;
                    var imageLock = new object();
                    {
                        // 订阅图像流，等待一帧图像
                        var tcs = new TaskCompletionSource<BitmapSource?>();
                        using var imageSubscription = _cameraService.ImageStreamWithId
                            .Select(data => data.Image) // 从元组中选择图像
                            .Take(1)
                            .Timeout(TimeSpan.FromSeconds(5))
                            .Subscribe(
                                onNext: imageData => tcs.TrySetResult(imageData),
                                onError: ex => tcs.TrySetException(ex),
                                onCompleted: () => tcs.TrySetResult(null)
                            );
                        try
                        {
                            using var cts = new CancellationTokenSource();
                            await using (cts.Token.Register(() => tcs.TrySetCanceled()))
                            {
                                var receivedImageData = await tcs.Task;
                                if (receivedImageData != null)
                                {
                                    BitmapSource? frozenClone = null;
                                    await Application.Current.Dispatcher.InvokeAsync(() =>
                                    {
                                        try
                                        {
                                            var clone = receivedImageData.Clone();
                                            clone.Freeze();
                                            frozenClone = clone;
                                            CurrentImage = clone;
                                            Log.Information("Image cloned and frozen on UI thread");
                                        }
                                        catch (Exception uiEx)
                                        {
                                            Log.Error(uiEx, "在UI线程克隆/冻结图像时发生错误");
                                        }
                                    });
                                    if (frozenClone != null)
                                    {
                                        lock (imageLock)
                                        {
                                            capturedImage = frozenClone;
                                        }

                                        Log.Information("从图像流获取并处理了一帧图像");
                                    }
                                }
                                else
                                {
                                    Log.Warning("未能从图像流获取有效图像数据");
                                }
                            }
                        }
                        catch (TimeoutException)
                        {
                            Log.Warning("获取图像流超时");
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "从图像流获取图像失败");
                        }
                    }
                    // 拍照结束

                    // 称重
                    try
                    {
                        var weightSettings = _settingsService.LoadSettings<Weight.Models.Settings.WeightSettings>();
                        var stableWeightQueryWindowSeconds = weightSettings.StableWeightQueryWindowSeconds > 0
                            ? weightSettings.StableWeightQueryWindowSeconds
                            : 2;

                        // IntegrationTimeMs 将用作如果初始查找失败后的最大等待时间
                        var maxWaitTimeForWeightMs = weightSettings.IntegrationTimeMs > 0
                            ? weightSettings.IntegrationTimeMs
                            : 2000;

                        double? weightInKg = null;
                        // barcodeScanTime 代表包裹处理流程中，称重逻辑开始执行的时间点
                        var barcodeScanTime = DateTime.Now;

                        Log.Information(
                            "包裹 {Barcode}: 开始查询稳定重量。查询时间点: {ScanTime:HH:mm:ss.fff}, 初始查询窗口: {QueryWindow}秒。",
                            _currentPackage.Barcode, barcodeScanTime, stableWeightQueryWindowSeconds);

                        StableWeightEntry? bestStableWeight;
                        // 1. 初始查找: 查找在 barcodeScanTime 之前的稳定重量
                        lock (_stableWeightQueue)
                        {
                            Log.Information("包裹 {Barcode}: 正在进行初始稳定重量查找。队列当前有 {QueueCount} 条数据。",
                                _currentPackage.Barcode, _stableWeightQueue.Count);

                            // 添加日志：记录队列中的每条数据
                            if (_stableWeightQueue.Count != 0)
                            {
                                Log.Information("包裹 {Barcode}: 初始查找前队列数据详情 (", _currentPackage.Barcode);
                                foreach (var entry in _stableWeightQueue)
                                {
                                    Log.Information("  - 重量: {Weight}kg, 时间戳: {Timestamp:HH:mm:ss.fff}", entry.WeightKg,
                                        entry.Timestamp);
                                }
                            }

                            bestStableWeight = _stableWeightQueue
                                .Where(entry =>
                                    (barcodeScanTime - entry.Timestamp).TotalSeconds < stableWeightQueryWindowSeconds &&
                                    (barcodeScanTime - entry.Timestamp).TotalSeconds >= 0)
                                .OrderByDescending(entry => entry.Timestamp)
                                .Cast<StableWeightEntry?>()
                                .FirstOrDefault();
                        }

                        if (bestStableWeight.HasValue)
                        {
                            weightInKg = bestStableWeight.Value.WeightKg;
                            Log.Information("包裹 {Barcode}: 使用初始找到的稳定重量: {Weight}kg (时间戳: {Timestamp:HH:mm:ss.fff})",
                                _currentPackage.Barcode, weightInKg, bestStableWeight.Value.Timestamp);
                        }
                        else
                        {
                            // 2. 等待逻辑: 如果初始查找失败，则等待一段时间查找 barcodeScanTime 之后的重量
                            Log.Warning(
                                "包裹 {Barcode}: 初始查找未能在 {QueryWindow}秒内找到稳定重量 (处理时间: {ScanTime:HH:mm:ss.fff})。开始等待最长 {MaxWait}ms 获取后续重量。",
                                _currentPackage.Barcode, stableWeightQueryWindowSeconds, barcodeScanTime,
                                maxWaitTimeForWeightMs);

                            var sw = Stopwatch.StartNew();
                            while (sw.ElapsedMilliseconds < maxWaitTimeForWeightMs)
                            {
                                lock (_stableWeightQueue)
                                {
                                    bestStableWeight = _stableWeightQueue
                                        .Where(entry => entry.Timestamp > barcodeScanTime) // 查找在 barcodeScanTime 之后的数据
                                        .OrderBy(entry => entry.Timestamp) // 取时间戳最早的那一个
                                        .Cast<StableWeightEntry?>()
                                        .FirstOrDefault();
                                }

                                if (bestStableWeight.HasValue)
                                {
                                    weightInKg = bestStableWeight.Value.WeightKg;
                                    Log.Information(
                                        "包裹 {Barcode}: Stable weight obtained during wait: {Weight}kg (Timestamp: {Timestamp:HH:mm:ss.fff})",
                                        _currentPackage.Barcode, weightInKg, bestStableWeight.Value.Timestamp);
                                    break;
                                }

                                await Task.Delay(20); // 短暂延迟
                            }

                            sw.Stop();

                            if (!bestStableWeight.HasValue)
                            {
                                Log.Warning("包裹 {Barcode}: 在最大等待时间 {MaxWait}ms 后仍未能获取稳定重量。", _currentPackage.Barcode,
                                    maxWaitTimeForWeightMs);
                            }

                            // 添加日志：如果等待后未找到，再次记录队列数据以便分析
                            if (!bestStableWeight.HasValue)
                            {
                                lock (_stableWeightQueue)
                                {
                                    Log.Information("包裹 {Barcode}: 等待后未找到稳定重量。等待结束时队列数据详情 (", _currentPackage.Barcode);
                                    if (_stableWeightQueue.Count != 0)
                                    {
                                        foreach (var entry in _stableWeightQueue)
                                        {
                                            Log.Information("  - 重量: {Weight}kg, 时间戳: {Timestamp:HH:mm:ss.fff}",
                                                entry.WeightKg, entry.Timestamp);
                                        }
                                    }
                                    else
                                    {
                                        Log.Information("  队列为空。");
                                    }

                                    Log.Information(" 包裹 {Barcode}: 等待结束时队列数据详情 )", _currentPackage.Barcode);
                                }
                            }
                        }

                        if (weightInKg.HasValue)
                        {
                            var actualWeight = weightInKg.Value;
                            if (SelectedPallet != null && SelectedPallet.Name != "noPallet")
                                actualWeight = Math.Max(0, actualWeight - SelectedPallet.Weight);
                            _currentPackage.Weight = actualWeight;
                        }
                        else
                        {
                            Log.Warning("包裹 {Barcode}: 最终未能获取包裹重量。", _currentPackage.Barcode);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "包裹 {Barcode}: 获取重量数据时发生错误。", _currentPackage.Barcode);
                    }

                    try
                    {
                        await TriggerVolumeCamera();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "体积测量任务异常");
                    }

                    // 体积测量结束
                    try
                    {
                        var volSettings = cameraOverallSettings.VolumeCamera;
                        if (volSettings.ImageSaveMode != DimensionImageSaveMode.None) // Check if any save flag is set
                        {
                            Log.Information("根据配置获取尺寸刻度图像，模式: {Mode}", volSettings.ImageSaveMode);
                            var dimensionImagesResult = await _volumeCamera.GetDimensionImagesAsync();

                            var barcodeSpecificPath = string.Empty; // 在外部声明以便后续使用
                            // 无论 GetDimensionImagesAsync 是否成功，只要需要保存任何尺寸图像，就创建路径
                            if (volSettings.ImageSaveMode != DimensionImageSaveMode.None)
                            {
                                var baseSavePath = volSettings.ImageSavePath;
                                var dateFolder = _currentPackage.CreateTime.ToString("yyyy-MM-dd");
                                var sanitizedBarcode = string.Join("_",
                                    _currentPackage.Barcode.Split(Path.GetInvalidFileNameChars()));
                                var timestamp = _currentPackage.CreateTime.ToString("HHmmss"); // 获取时分秒

                                // 新的路径：基础路径/日期/条码_时分秒
                                barcodeSpecificPath = Path.Combine(baseSavePath, dateFolder, $"{timestamp}.{sanitizedBarcode}");
                                Directory.CreateDirectory(barcodeSpecificPath);
                            }

                            if (volSettings.ImageSaveMode.HasFlag(DimensionImageSaveMode.Vertical) &&
                                dimensionImagesResult.VerticalViewImage != null)
                            {
                                const string fileName = "Vertical.jpg";
                                var filePath = Path.Combine(barcodeSpecificPath, fileName);
                                await SaveDimensionImageAsync(dimensionImagesResult.VerticalViewImage, filePath);
                                Log.Information("俯视图已保存: {FilePath}", filePath);
                            }

                            if (volSettings.ImageSaveMode.HasFlag(DimensionImageSaveMode.Side) &&
                                dimensionImagesResult.SideViewImage != null)
                            {
                                const string fileName = "Side.jpg";
                                var filePath = Path.Combine(barcodeSpecificPath, fileName);
                                await SaveDimensionImageAsync(dimensionImagesResult.SideViewImage, filePath);
                                Log.Information("侧视图已保存: {FilePath}", filePath);
                            }

                            if (!dimensionImagesResult.IsSuccess &&
                                (volSettings.ImageSaveMode.HasFlag(DimensionImageSaveMode.Vertical) ||
                                 volSettings.ImageSaveMode.HasFlag(DimensionImageSaveMode.Side)))
                            {
                                Log.Warning("未能获取尺寸刻度图像的完整成功：{Error}", dimensionImagesResult.ErrorMessage);
                            }

                            // 保存体积相机原图 (如果已配置)
                            if (volSettings.ImageSaveMode.HasFlag(DimensionImageSaveMode.Original))
                            {
                                if (VolumeImage != null) // 直接使用 VolumeImage
                                {
                                    BitmapSource? frozenClone = null;
                                    await Application.Current.Dispatcher.InvokeAsync(() =>
                                    {
                                        try
                                        {
                                            frozenClone = (BitmapSource)VolumeImage.Clone(); // 克隆 VolumeImage
                                            frozenClone.Freeze();
                                        }
                                        catch (Exception uiEx)
                                        {
                                            Log.Error(uiEx, "克隆/冻结原始体积相机图像时发生错误");
                                        }
                                    });

                                    if (frozenClone != null)
                                    {
                                        if (string.IsNullOrEmpty(barcodeSpecificPath)) // 双重检查，理论上前面已创建
                                        {
                                            var baseSavePath = volSettings.ImageSavePath;
                                            var dateFolder = _currentPackage.CreateTime.ToString("yyyy-MM-dd");
                                            var sanitizedBarcode = string.Join("_",
                                                _currentPackage.Barcode.Split(Path.GetInvalidFileNameChars()));
                                            var timestamp = _currentPackage.CreateTime.ToString("HHmmss"); // 获取时分秒
                                            barcodeSpecificPath = Path.Combine(baseSavePath, dateFolder,
                                                $"{timestamp}.{sanitizedBarcode}");
                                            Directory.CreateDirectory(barcodeSpecificPath);
                                        }

                                        const string fileName = "Original_Volume.jpg";
                                        var filePath = Path.Combine(barcodeSpecificPath, fileName);
                                        await SaveDimensionImageAsync(frozenClone, filePath);
                                        Log.Information("原始体积相机测量图像已保存: {FilePath}", filePath);
                                    }
                                }
                                else
                                {
                                    Log.Warning("未能从测量流程获取原始体积相机图像 (VolumeImage 为 null)。");
                                }
                            }
                        }
                    }
                    catch (Exception imgEx)
                    {
                        Log.Error(imgEx, "获取或保存尺寸刻度图像时发生错误");
                    }
                    // --- 尺寸刻度图保存逻辑结束 ---

                    // 更新体积（使用cm³作为单位）
                    _currentPackage.SetDimensions(_currentPackage.Length ?? 0, _currentPackage.Width ?? 0,
                        _currentPackage.Height ?? 0);

                    // 检查三个必要条件是否都满足：条码、重量和体积
                    var isBarcodeMissing = string.IsNullOrEmpty(_currentPackage.Barcode);
                    var isWeightMissing = _currentPackage.Weight <= 0;
                    var isVolumeMissing = (_currentPackage.Length ?? 0) <= 0 ||
                                          (_currentPackage.Width ?? 0) <= 0 ||
                                          (_currentPackage.Height ?? 0) <= 0;

                    var isComplete = !isBarcodeMissing && !isWeightMissing && !isVolumeMissing;

                    if (!isComplete)
                    {
                        Log.Warning("包裹信息不完整。条码缺失: {BarcodeMissing}, 重量缺失: {WeightMissing}, 体积缺失: {VolumeMissing}",
                            isBarcodeMissing, isWeightMissing, isVolumeMissing);

                        // Determine the specific error message for UI display and ErrorMessage property
                        string completionMessage; // Default success message for UI, will be overridden for failures
                        if (isBarcodeMissing) completionMessage = "Missing Barcode";
                        else if (isWeightMissing) completionMessage = "Missing Weight";
                        else if (isVolumeMissing) completionMessage = "Missing Volume";
                        else completionMessage = "Data Incomplete"; // Fallback, theoretically should not happen

                        _currentPackage.ErrorMessage = completionMessage; // Store detailed error message
                        _currentPackage.SetStatus(completionMessage); // 直接设置具体的缺失状态
                        _ = _audioService.PlayPresetAsync(AudioType.SystemError);
                        // 移除立即更新UI的调用，保留音效播放
                    }
                    else
                    {
                        _currentPackage.SetStatus("Complete"); // Set primary status to "Complete"
                        _currentPackage.ErrorMessage = string.Empty; // Clear any previous error message on success
                        _ = _audioService.PlayPresetAsync(AudioType.Success); // 播放成功音效
                    }
                    // 注意：不再重新设置CreateTime，使用处理开始时设置的统一时间戳

                    // 保存捕获的图像（总是执行，如果已捕获，即使包裹不完整也可能保存）
                    BitmapSource? imageToProcess = null;
                    string? base64Image = null;
                    string? imageName = null;

                    try
                    {
                        lock (imageLock)
                        {
                            if (capturedImage != null)
                            {
                                imageToProcess = capturedImage;
                            }
                        }

                        if (imageToProcess != null)
                            try
                            {
                                var cameraSettings =
                                    _settingsService.LoadSettings<CameraOverallSettings>("CameraSettings");
                                // 使用新的CancellationTokenSource，不设置超时
                                using var imageSaveCts = new CancellationTokenSource();
                                // 将冻结的图像直接传递给 SaveImageAsync
                                var result = await SaveImageAsync(imageToProcess, cameraSettings,
                                    _currentPackage, // 传递包含正确条码的 _currentPackage
                                    imageSaveCts.Token);
                                if (result.HasValue) (base64Image, imageName) = result.Value;
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, "保存图像到文件时发生错误");
                                // 不在此处更新包状态，因为它可能已经是Failed或Error
                            }
                        else
                            Log.Warning("未能捕获到图像");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "处理图像时发生错误: {Error}", ex.Message);
                        // 不在此处更新包状态
                    }

                    // 调用桑能接口上传数据 (仅当信息完整时)
                    if (isComplete) // 检查完整性标志
                    {
                        try
                        {
                            var request = new SangNengWeightRequest
                            {
                                Barcode = _currentPackage.Barcode, // 使用包裹对象的条码
                                Weight = _currentPackage.Weight,
                                Length = _currentPackage.Length ?? 0,
                                Width = _currentPackage.Width ?? 0,
                                Height = _currentPackage.Height ?? 0,
                                Volume = _currentPackage.Volume ?? 0,
                                Timestamp = _currentPackage.CreateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                                Image = base64Image ?? string.Empty,
                                ImageName = imageName ?? string.Empty
                            };

                            var response = await _sangNengService.SendWeightDataAsync(request);
                            if (response.Code != 1) // 修改为判断 Code == 1 表示成功
                            {
                                Log.Warning("上传数据到桑能服务器失败: {Message}", response.Message);
                                // 如果上传失败，保持状态为 Success 但记录错误
                            }
                            else
                            {
                                Log.Information("成功上传数据到桑能服务器");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "上传数据到桑能服务器时发生错误");
                            // 记录错误，但不改变 Success 状态
                        }
                    }
                    else
                    {
                        Log.Information("包裹信息不完整，跳过上传到桑能服务器");
                    }

                    // 在后台保存到数据库（总是执行）
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var historyRecord = PackageHistoryRecord.FromPackageInfo(_currentPackage);
                            await _packageHistoryDataService.AddPackageAsync(historyRecord);
                            Log.Information("包裹数据已保存到数据库：{Barcode}, 状态: {Status}, 消息: {StatusDisplay}",
                                historyRecord.Barcode, historyRecord.Status,
                                historyRecord.StatusDisplay);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "保存包裹数据到数据库时发生错误：{Barcode}",
                                _currentPackage.Barcode);
                        }
                    }, CancellationToken.None);
                }
                catch (Exception ex) // 捕获包裹处理过程中的其他通用异常
                {
                    Log.Error(ex, "处理条码信息时发生未预期的错误: {Barcode}", barcode);
                    _currentPackage.SetStatus("Failed"); // Set primary status to "Failed"
                    _currentPackage.ErrorMessage = "Processing Error: " + ex.Message; // Store detailed error message
                    _ = _audioService.PlayPresetAsync(AudioType.SystemError);
                }
            }
            catch (Exception ex) // 捕获包裹处理过程中的其他通用异常
            {
                Log.Error(ex, "处理条码信息时发生未预期的错误: {Barcode}", barcode);

                _currentPackage.SetStatus("Failed"); // Set primary status to "Failed"
                _currentPackage.ErrorMessage = "Processing Error: " + ex.Message; // Store detailed error message
                _ = _audioService.PlayPresetAsync(AudioType.SystemError);
            }
            // --- 原始逻辑结束 ---



            // 最后一次性更新所有UI：实时区域状态和历史记录（移到方法末尾）
            Application.Current.Dispatcher.Invoke(() =>
            {
                // 1. 先更新实时区域显示
                try
                {
                    // Pass the detailed error message for UI display if available, otherwise use StatusDisplay
                    UpdateCurrentPackageUiDisplay(_currentPackage.Status == "Failed" ? _currentPackage.ErrorMessage : _currentPackage.StatusDisplay);
                    Log.Debug("实时区域显示更新成功");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "更新实时区域显示时发生错误，条码: {Barcode}", _currentPackage.Barcode);
                }

                // 2. 然后更新历史记录
                try
                {
                    PackageHistory.Insert(0, _currentPackage);
                    Log.Information("包裹已添加到历史记录：{Barcode}, 状态: {Status}", _currentPackage.Barcode, _currentPackage.Status);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "添加包裹到历史记录时发生错误，条码: {Barcode}", _currentPackage.Barcode);
                }

                // 3. 最后更新统计信息
                try
                {
                    UpdateStatistics();
                    Log.Debug("统计信息更新成功");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "更新统计信息时发生错误");
                }
            });
        }
        finally // 关联外层 Try，确保释放信号量
        {
            _barcodeProcessingLock.Release();
        }
    }

    private async Task TriggerVolumeCamera()
    {
        try
        {
            // 使用信号量确保同一时间只有一个测量任务在进行
            if (!await _measurementLock.WaitAsync(TimeSpan.FromSeconds(1)))
            {
                Log.Warning("上次测量未完成，跳过本次测量");
                _ = _audioService.PlayPresetAsync(AudioType.SystemError);
                return;
            }

            try
            {
                _currentPackage.SetStatus("Measuring"); // UI String

                // 触发测量
                var result = _volumeCamera.TriggerMeasure();

                if (!result.IsSuccess)
                {
                    Log.Error("体积测量失败：{Error}", result.ErrorMessage);
                    _currentPackage.SetDimensions(0, 0, 0);
                    _currentPackage.SetStatus("Failed"); // Set primary status to "Failed"
                    _currentPackage.ErrorMessage = result.ErrorMessage; // Store detailed error message
                    _ = _audioService.PlayPresetAsync(AudioType.SystemError);
                    return;
                }

                // 更新测量结果（将毫米转换为厘米）
                _currentPackage.SetDimensions(result.Length / 10.0, result.Width / 10.0, result.Height / 10.0);
                _currentPackage.SetStatus("Complete"); // Set primary status to "Complete"
            }
            finally
            {
                _measurementLock.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "体积测量过程发生错误");
            _currentPackage.SetDimensions(0, 0, 0);
            _currentPackage.SetStatus("Failed"); // Set primary status to "Failed"
            _currentPackage.ErrorMessage = "Volume Measurement Error: " + ex.Message; // Store detailed error message
            _ = _audioService.PlayPresetAsync(AudioType.SystemError);
        }
    }

    /// <summary>
    /// 保存图像到文件，添加水印，并返回Base64编码的图像和名称
    /// </summary>
    private static async Task<(string base64Image, string imageName)?> SaveImageAsync(BitmapSource image,
        CameraOverallSettings settings, PackageInfo package,
        CancellationToken cancellationToken)
    {
        string? tempFilePath;
        try
        {
            string? base64Image = null;
            var imageName = string.Empty; // This will become part of the folder structure, filename will be simpler

            await Task.Run(async () => // Changed to async Task for await inside
            {
                try
                {
                    var basePhotoSavePath = settings.ImageSave.SaveFolderPath;
                    var dateFolder = package.CreateTime.ToString("yyyy-MM-dd");
                    var sanitizedBarcode = string.Join("_", package.Barcode.Split(Path.GetInvalidFileNameChars()));
                    var timestamp = package.CreateTime.ToString("HHmmss"); // 获取时分秒

                    // 新的路径：基础路径/日期/条码_时分秒
                    var barcodeSpecificPhotoPath = Path.Combine(basePhotoSavePath, dateFolder, $"{timestamp}.{sanitizedBarcode}");
                    Directory.CreateDirectory(barcodeSpecificPhotoPath); // 确保条码级别目录存在

                    // 文件名可以简化，因为条码和日期已在路径中，或者保留时间戳以防万一（例如同一条码多次快照）
                    var photoFileName = $"Photo_{package.CreateTime:HHmmssfff}.jpg"; // 使用包裹创建时间而不是当前时间
                    var finalFilePath = Path.Combine(barcodeSpecificPhotoPath, photoFileName);
                    imageName = photoFileName; // Update imageName to be just the file name for return value

                    // 先保存BitmapSource到临时文件 (临时文件可以在基础保存路径下，避免路径过长问题)
                    var tempFileName = $"temp_{sanitizedBarcode}_{package.CreateTime:yyyyMMddHHmmssfff}.jpg";
                    tempFilePath =
                        Path.Combine(settings.ImageSave.SaveFolderPath, tempFileName); // Temp file in base directory

                    BitmapEncoder encoder = new JpegBitmapEncoder { QualityLevel = 90 };

                    encoder.Frames.Add(BitmapFrame.Create(image));

                    await using (var fileStream =
                                 new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        encoder.Save(fileStream);
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        Log.Warning("保存图像操作被取消 (encoding后)");
                        return; // Exiting Task.Run lambda
                    }

                    // 使用 System.Drawing 添加水印 (水印内容保持英文，因为是图像叠加)
                    using (var bitmap = new Bitmap(tempFilePath))
                    using (var graphics = Graphics.FromImage(bitmap))
                    using (var font = new Font("Arial", 40))
                    using (var brush = new SolidBrush(Color.Green))
                    {
                        graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                        var watermarkLines = new[]
                        {
                            $"Barcode: {package.Barcode}",
                            $"Size: {package.Length:F1}cm × {package.Width:F1}cm × {package.Height:F1}cm",
                            $"Weight: {package.Weight:F3}kg",
                            $"Volume: {package.Length * package.Width * package.Height:N0}cm³", // Calculate volume from L*W*H in cm³
                            $"Time: {package.CreateTime:yyyy-MM-dd HH:mm:ss}"
                        };

                        if (!string.IsNullOrEmpty(package.PalletName) && package.PalletName != "noPallet")
                        {
                            var palletLines = new List<string>(watermarkLines);
                            palletLines.Insert(1, $"Pallet: {package.PalletName} ({package.PalletWeight:F3}kg)");
                            watermarkLines = palletLines.ToArray();
                        }

                        const int padding = 20;
                        const int lineSpacing = 50;
                        var startY = padding;

                        foreach (var line in watermarkLines)
                        {
                            graphics.DrawString(line, font, brush, padding, startY);
                            startY += lineSpacing;
                        }

                        // 保存带水印的图像到最终路径
                        bitmap.Save(finalFilePath, System.Drawing.Imaging.ImageFormat.Jpeg);

                        // package.ImagePath = finalFilePath; // 访问性问题
                        package.ImagePath = finalFilePath; // 设置图像路径
                        Log.Information("主相机图像已保存: {FilePath}", finalFilePath);

                        // 转换为Base64 (从最终文件读取或从内存中的bitmap，这里选择从内存)
                        using var ms = new MemoryStream();
                        bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                        base64Image = Convert.ToBase64String(ms.ToArray());
                    }
                }
                catch (OperationCanceledException)
                {
                    Log.Warning("图像处理操作被取消 (SaveImageAsync Task.Run)");
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "图像处理时发生错误 (SaveImageAsync Task.Run): {Error}", ex.Message);
                    throw;
                }
            }, cancellationToken);
            return (base64Image!, imageName); // imageName is now just the file name
        }
        catch (OperationCanceledException)
        {
            Log.Warning("保存图像任务取消");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存图像时发生错误");
            return null;
        }
    }

    /// <summary>
    /// 更新当前包裹处理结果的UI显示（重量、尺寸、时间、状态）
    /// </summary>
    private void UpdateCurrentPackageUiDisplay(string customDisplay)
    {
        var items = PackageInfoItems.ToList();

        // 更新重量 (UI String from PackageInfoItem definition)
        // _currentPackage.Weight ist bereits der Nettowert (ggf. ohne Palettengewicht)
        var weightText = double.IsNaN(_currentPackage.Weight) || double.IsInfinity(_currentPackage.Weight)
            ? "0.000"
            : $"{_currentPackage.Weight:F3}";
        items[0].Value = weightText; // Index 0 ist "Weight"

        // 更新尺寸 (UI String from PackageInfoItem definition)
        var length = _currentPackage.Length ?? 0;
        var width = _currentPackage.Width ?? 0;
        var height = _currentPackage.Height ?? 0;

        // 检查异常值
        if (double.IsNaN(length) || double.IsInfinity(length)) length = 0;
        if (double.IsNaN(width) || double.IsInfinity(width)) width = 0;
        if (double.IsNaN(height) || double.IsInfinity(height)) height = 0;

        items[1].Value = $"{length:F1}x{width:F1}x{height:F1}";

        // 更新时间 (UI String from PackageInfoItem definition)
        try
        {
            items[2].Value = _currentPackage.CreateTime.ToString("HH:mm:ss");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "格式化时间时发生错误，使用默认时间");
            items[2].Value = DateTime.Now.ToString("HH:mm:ss");
        }

        // 更新状态
        var statusItem = items[3];
        string displayText;
        string color;

        switch (customDisplay)
        {
            case "Complete":
            case "Success": // 新增：处理成功状态
                displayText = "Complete"; // UI String
                color = "#4CAF50"; // Green
                break;
            case "Failed":
            case "Error":
            case "Missing Barcode": // 新增：条码缺失
            case "Missing Weight": // 新增：重量缺失
            case "Missing Volume": // 新增：体积缺失
                // 使用自定义显示（如果可用），否则为 "Failed" (UI Strings)
                displayText = !string.IsNullOrEmpty(customDisplay) && customDisplay != "Complete"
                    ? customDisplay
                    : "Failed";
                color = "#FF0000"; // Red
                break;
            default: // 理论上不应发生，但做防御性处理
                displayText = "Waiting"; // UI String
                color = "#808080"; // Gray
                break;
        }

        statusItem.Value = displayText;
        statusItem.StatusColor = color;

        PackageInfoItems = [.. items];
    }

    private void OnVolumeCameraConnectionChanged(string? deviceId, bool isConnected) // deviceId 可以为 null
    {
        try
        {
            var volumeCameraStatus =
                DeviceStatuses.FirstOrDefault(x =>
                    x.Name == "Volume Camera"); // UI String (Keep as is, Name is an identifier)
            if (volumeCameraStatus == null) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                volumeCameraStatus.Status =
                    isConnected ? "Online" : "Offline"; // UI Strings (Keep as is, Status is a state identifier)
                volumeCameraStatus.StatusColor = isConnected ? "#4CAF50" : "#FFA500";

                if (!isConnected) return;
                if (!IsInitializingOverlayVisible) return; // 仅当遮罩当前可见时才隐藏
                IsInitializingOverlayVisible = false;
                Log.Information("体积相机已连接，正在隐藏初始化遮罩。");
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新体积相机状态时发生错误");
        }
    }

    private void OnCameraConnectionChanged(string? deviceId, bool isConnected)
    {
        try
        {
            var cameraStatus =
                DeviceStatuses.FirstOrDefault(x =>
                    x.Name == "Photo Camera"); // UI String (Keep as is, Name is an identifier)
            if (cameraStatus == null) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                cameraStatus.Status =
                    isConnected ? "Online" : "Offline"; // UI Strings (Keep as is, Status is a state identifier)
                cameraStatus.StatusColor = isConnected ? "#4CAF50" : "#FFA500";
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新拍照相机状态时发生错误");
        }
    }

    private void OnWeightScaleConnectionChanged(string deviceId, bool isConnected)
    {
        try
        {
            var weightScaleStatus =
                DeviceStatuses.FirstOrDefault(x =>
                    x.Name == "Weight Scale"); // UI String (Keep as is, Name is an identifier)
            if (weightScaleStatus == null) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                weightScaleStatus.Status =
                    isConnected ? "Online" : "Offline"; // UI Strings (Keep as is, Status is a state identifier)
                weightScaleStatus.StatusColor = isConnected ? "#4CAF50" : "#FFA500";
            });

            // 如果断开连接，清除最新的重量数据和队列
            if (isConnected) return;
            _rawWeightBuffer.Clear();
            lock (_stableWeightQueue) // 确保线程安全
            {
                _stableWeightQueue.Clear();
            }

            Log.Information("重量秤已断开连接，正在清除原始重量缓冲区和稳定重量队列。");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新重量秤状态时发生错误");
        }
    }

    #region Properties // 特性区域

    public string CurrentBarcode
    {
        get => _currentBarcode;
        set => SetProperty(ref _currentBarcode, value);
    }

    public ImageSource? CurrentImage
    {
        get => _currentImage;
        private set => SetProperty(ref _currentImage, value);
    }

    public ImageSource? VolumeImage
    {
        get => _volumeImage;
        private set
        {
            if (_volumeImage == value) return;
            if (SetProperty(ref _volumeImage, value)) Log.Debug("VolumeImage属性已更新");
        }
    }

    public ObservableCollection<PackageInfoItem> PackageInfoItems
    {
        get => _packageInfoItems;
        private set => SetProperty(ref _packageInfoItems, value);
    }

    public ObservableCollection<StatisticsItem> StatisticsItems
    {
        get => _statisticsItems;
        private set => SetProperty(ref _statisticsItems, value);
    }

    public ObservableCollection<PackageInfo> PackageHistory
    {
        get => _packageHistory;
        set => SetProperty(ref _packageHistory, value);
    }

    public ObservableCollection<DeviceStatus> DeviceStatuses
    {
        get => _deviceStatuses;
        private set => SetProperty(ref _deviceStatuses, value);
    }

    public SystemStatus SystemStatus
    {
        get => _systemStatus;
        private set => SetProperty(ref _systemStatus, value);
    }

    public ICommand OpenSettingsCommand { get; } = null!;

    /// <summary>
    /// 打开历史记录窗口命令
    /// </summary>
    public ICommand OpenHistoryWindowCommand { get; } = null!;

    public ObservableCollection<SelectablePalletModel> AvailablePallets
    {
        get => _availablePallets;
        set => SetProperty(ref _availablePallets, value);
    }

    private SelectablePalletModel? SelectedPallet
    {
        get => _selectedPallet;
        set => SetProperty(ref _selectedPallet, value);
    }

    public DelegateCommand<SelectablePalletModel> SelectPalletCommand { get; } = null!;

    // 新增：条码输入处理命令
    public DelegateCommand HandleScanStartCommand { get; } = null!;
    public DelegateCommand HandleBarcodeCompleteCommand { get; } = null!;

    public bool IsInitializingOverlayVisible // 新增属性
    {
        get => _isInitializingOverlayVisible;
        set => SetProperty(ref _isInitializingOverlayVisible, value);
    }

    #endregion

    #region Private Methods // 私有方法区域

    private void ExecuteOpenSettings()
    {
        _dialogService.ShowDialog("SettingsControl");
    }

    private void InitializePackageInfoItems()
    {
        PackageInfoItems =
        [
            new PackageInfoItem("Weight", "0.00", "kg", "Package Weight", "Scales24"),
            new PackageInfoItem("Size", "0 × 0 × 0", "cm", "Length × Width × Height", "Ruler24"),
            new PackageInfoItem("Time", "--:--:--", "", "Processing Time", "Timer24"),
            new PackageInfoItem("Status", "Waiting", "", "Processing Status", "Alert24")
        ];
    }

    private void InitializeStatisticsItems() // UI Strings below
    {
        StatisticsItems =
        [
            new StatisticsItem("Total", "0", "pcs", "Total Packages", "BoxMultiple24"),
            new StatisticsItem("Success", "0", "pcs", "Successfully Sorted", "CheckmarkCircle24"),
            new StatisticsItem("Failed", "0", "pcs", "Failed Packages", "ErrorCircle24"),
            new StatisticsItem("Rate", "0.00", "pcs/h", "Processing Rate", "ArrowTrendingLines24")
        ];
    }

    private void InitializeDeviceStatuses() // UI Strings below
    {
        DeviceStatuses =
        [
            new DeviceStatus { Name = "Photo Camera", Status = "Offline", Icon = "Camera24", StatusColor = "#FFA500" },
            new DeviceStatus { Name = "Volume Camera", Status = "Offline", Icon = "Cube24", StatusColor = "#FFA500" },
            new DeviceStatus { Name = "Weight Scale", Status = "Offline", Icon = "Scales24", StatusColor = "#FFA500" }
        ];
    }

    private void ExecuteSelectPallet(SelectablePalletModel pallet) // Pallet names are data/UI
    {
        foreach (var availablePallet in AvailablePallets) availablePallet.IsSelected = availablePallet == pallet;

        SelectedPallet = pallet;

        try
        {
            // 设置托盘高度给人加相机SDK
            var palletHeightMm = (int)(pallet.Height * 10);
            var result = _volumeCamera.SetPalletHeight(palletHeightMm);
            if (result != 0)
            {
                Log.Warning("Failed to set pallet height: Code {Result}", result);
                // Notification strings are UI and kept in English
                _notificationService.ShowWarning(
                    $"Failed to set pallet height for '{pallet.Name}' (Error code: {result}). Please try selecting the pallet again.");
            }
            else
            {
                Log.Information("启动时成功设置上次托盘高度: {Height}mm", palletHeightMm);
            }

            // 保存选择的托盘名称到配置
            var mainWindowSettings = _settingsService.LoadSettings<MainWindowSettings>();
            mainWindowSettings.LastSelectedPalletName = pallet.Name;
            _settingsService.SaveSettings(mainWindowSettings);
            Log.Information("User selected pallet saved: {PalletName}", pallet.Name);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error selecting or setting pallet height");
            _notificationService.ShowError(
                $"Error selecting/setting pallet height: {ex.Message}. Please try selecting the pallet again.");
        }
    }

    private void LoadAvailablePallets()
    {
        try
        {
            var palletSettings = _settingsService.LoadSettings<PalletSettings>();
            var mainWindowSettings = _settingsService.LoadSettings<MainWindowSettings>();
            var lastSelectedPalletName =
                mainWindowSettings.LastSelectedPalletName ?? "noPallet"; // "noPallet" is a key/identifier

            AvailablePallets.Clear();

            // 添加空托盘选项
            var emptyPallet = new SelectablePalletModel(new PalletModel
            {
                Name = "noPallet", // Key/identifier
                Weight = 0,
                Length = 0,
                Width = 0,
                Height = 0
            });
            AvailablePallets.Add(emptyPallet);

            // 添加配置的托盘
            foreach (var pallet in palletSettings.Pallets)
            {
                AvailablePallets.Add(new SelectablePalletModel(pallet));
            }

            // 查找并选中上次选择的托盘
            var palletToSelect = AvailablePallets.FirstOrDefault(p => p.Name == lastSelectedPalletName);

            if (palletToSelect != null)
            {
                palletToSelect.IsSelected = true;
                SelectedPallet = palletToSelect;
                Log.Information("已加载并选中上次保存的托盘：{PalletName}", palletToSelect.Name);
                // 重新触发一次相机设置，确保相机使用保存的托盘高度
                if (palletToSelect.Name == "noPallet") return; // Key/identifier
                try
                {
                    var palletHeightMm = (int)(palletToSelect.Height * 10);
                    var result = _volumeCamera.SetPalletHeight(palletHeightMm);
                    if (result != 0)
                    {
                        Log.Warning("启动时设置上次托盘高度失败: Code {Result}", result);
                        // 此处可以选择是否通知用户，或者只记录日志
                    }
                    else
                    {
                        Log.Information("启动时成功设置上次托盘高度: {Height}mm", palletHeightMm);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "启动时设置上次托盘高度时发生错误");
                }
            }
            else
            {
                // 如果找不到上次保存的托盘（可能已被删除），则默认选择空托盘
                emptyPallet.IsSelected = true;
                SelectedPallet = emptyPallet;
                Log.Warning("未找到上次保存的托盘 '{LastPalletName}'，默认选择空托盘", lastSelectedPalletName);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载托盘配置或应用上次选择失败");
            // 出现异常时，确保默认选择空托盘
            var emptyPallet = AvailablePallets.FirstOrDefault(p => p.Name == "noPallet"); // Key/identifier
            if (emptyPallet != null)
            {
                emptyPallet.IsSelected = true;
                SelectedPallet = emptyPallet;
            }
        }
    }

    // --- 新增：检查并更新初始设备状态的方法 ---
    private void UpdateInitialDeviceStatuses()
    {
        try
        {
            // 更新拍照相机状态
            OnCameraConnectionChanged(_cameraService.GetType().Name, _cameraService.IsConnected);

            // 更新体积相机状态
            OnVolumeCameraConnectionChanged(_volumeCamera.GetType().Name, _volumeCamera.IsConnected);

            // 更新重量称状态
            OnWeightScaleConnectionChanged(_weightService.GetType().Name, _weightService.IsConnected);

        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新初始设备状态时发生错误");
        }
    }
    // --- 新增结束 ---

    /// <summary>
    /// 异步将 BitmapSource 保存到文件。
    /// </summary>
    /// <param name="image">要保存的 BitmapSource。</param>
    /// <param name="filePath">图像将保存到的完整路径。</param>
    /// <returns>表示异步操作的任务。</returns>
    private static async Task SaveDimensionImageAsync(BitmapSource image, string filePath)
    {
        try
        {
            await Task.Run(() =>
            {
                // 默认使用 Jpeg 格式，质量设置为 90
                var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
                encoder.Frames.Add(BitmapFrame.Create(image));

                try
                {
                    using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                    encoder.Save(fileStream);
                }
                catch (IOException ioEx)
                {
                    // 更细致地记录IO特定错误
                    Log.Error(ioEx, "保存尺寸图像到文件时发生IO错误: {FilePath}", filePath);
                    // 根据需要重新抛出或处理，此处仅记录日志
                }
                catch (UnauthorizedAccessException uaEx)
                {
                    Log.Error(uaEx, "保存尺寸图像到文件时权限不足: {FilePath}", filePath);
                }
            });
        }
        catch (Exception ex)
        {
            // 捕获 Task.Run 或编码器创建时的异常
            Log.Error(ex, "保存尺寸图像时发生未预期的错误: {FilePath}", filePath);
        }
    }

    private static HistoryViewConfiguration CreateSangNengHistoryViewConfiguration()
    {
        return new HistoryViewConfiguration
        {
            ColumnSpecs =
            [
                new HistoryColumnSpec
                {
                    PropertyName = nameof(PackageHistoryRecord.Index),
                    HeaderResourceKey = "PackageHistory_Header_Index", IsDisplayed = true, DisplayOrderInGrid = 0,
                    Width = "Auto"
                },
                new HistoryColumnSpec
                {
                    PropertyName = nameof(PackageHistoryRecord.Barcode),
                    HeaderResourceKey = "PackageHistory_Header_Barcode", IsDisplayed = true, DisplayOrderInGrid = 1,
                    Width = "*"
                },
                new HistoryColumnSpec
                {
                    PropertyName = "ImageAction", HeaderResourceKey = "PackageHistory_Header_ImageAction",
                    IsDisplayed = true, DisplayOrderInGrid = 2, Width = "Auto", IsTemplateColumn = true
                },
                new()
                {
                    PropertyName = nameof(PackageHistoryRecord.Weight),
                    HeaderResourceKey = "PackageHistory_Header_Weight", IsDisplayed = true, DisplayOrderInGrid = 3,
                    Width = "*", StringFormat = "F3"
                },
                new()
                {
                    PropertyName = nameof(PackageHistoryRecord.Length),
                    HeaderResourceKey = "PackageHistory_Header_Length", IsDisplayed = true, DisplayOrderInGrid = 4,
                    Width = "*", StringFormat = "F1"
                },
                new()
                {
                    PropertyName = nameof(PackageHistoryRecord.Width),
                    HeaderResourceKey = "PackageHistory_Header_Width", IsDisplayed = true, DisplayOrderInGrid = 5,
                    Width = "*", StringFormat = "F1"
                },
                new()
                {
                    PropertyName = nameof(PackageHistoryRecord.Height),
                    HeaderResourceKey = "PackageHistory_Header_Height", IsDisplayed = true, DisplayOrderInGrid = 6,
                    Width = "*", StringFormat = "F1"
                },
                new()
                {
                    PropertyName = nameof(PackageHistoryRecord.CreateTime),
                    HeaderResourceKey = "PackageHistory_Header_CreateTime", IsDisplayed = true, DisplayOrderInGrid = 7,
                    Width = "*", StringFormat = "yyyy-MM-dd HH:mm:ss"
                },
                new()
                {
                    PropertyName = nameof(PackageHistoryRecord.PalletName),
                    HeaderResourceKey = "PackageHistory_Header_PalletName", IsDisplayed = true, DisplayOrderInGrid = 8
                },
                new()
                {
                    PropertyName = nameof(PackageHistoryRecord.PalletWeight),
                    HeaderResourceKey = "PackageHistory_Header_PalletWeight", IsDisplayed = true,
                    DisplayOrderInGrid = 9, StringFormat = "F3"
                },
                new HistoryColumnSpec
                {
                    PropertyName = nameof(PackageHistoryRecord.Status),
                    HeaderResourceKey = "PackageHistory_Header_Status", IsDisplayed = true, DisplayOrderInGrid = 10
                }, // StatusDisplay might be preferred by ViewModel
                new HistoryColumnSpec
                {
                    PropertyName = nameof(PackageHistoryRecord.ChuteNumber),
                    HeaderResourceKey = "PackageHistory_Header_ChuteNumber", IsDisplayed = false,
                    DisplayOrderInGrid = 11
                }, // Example: Hide ChuteNumber for SangNeng
                new HistoryColumnSpec
                {
                    PropertyName = nameof(PackageHistoryRecord.ErrorMessage),
                    HeaderResourceKey = "PackageHistory_Header_Error", IsDisplayed = true, DisplayOrderInGrid = 12
                },
                new HistoryColumnSpec
                {
                    PropertyName = nameof(PackageHistoryRecord.ImagePath),
                    HeaderResourceKey = "PackageHistory_Header_ImagePath", IsDisplayed = false, IsExported = true
                }
            ]
        };
    }

    // 新增：条码输入处理的MVVM方法

    /// <summary>
    /// 清空条码缓存的方法
    /// </summary>
    internal void ClearBarcodeBuffer()
    {
        _barcodeBuffer.Clear();
        CurrentBarcode = string.Empty;
        Log.Information("手动删除输入框内容，已清空条码缓存。");
    }

    /// <summary>
    /// 处理扫描开始（检测到起始字符如@时）
    /// </summary>
    private void ExecuteHandleScanStart()
    {
        _isScanningInProgress = true;
        _barcodeBuffer.Clear();

        // 清空当前条码显示
        CurrentBarcode = string.Empty;

        // 请求View清空输入框并设置焦点
        RequestClearBarcodeInput?.Invoke();
        RequestFocusBarcodeInput?.Invoke();

    }

    /// <summary>
    /// 处理条码扫描完成（检测到结束符如回车时）
    /// </summary>
    private void ExecuteHandleBarcodeComplete()
    {
        string barcode;
        if (_isScanningInProgress)
        {
            barcode = _barcodeBuffer.ToString().Trim();
            _barcodeBuffer.Clear();
            _isScanningInProgress = false;
            Log.Information("扫码模式完成，从缓存获取条码: {Barcode}", barcode);
        }
        else
        {
            barcode = CurrentBarcode.Trim();
            Log.Information("手动输入模式完成，从 CurrentBarcode 获取条码: {Barcode}", barcode);
        }

        if (string.IsNullOrWhiteSpace(barcode))
        {
            Log.Warning("处理完成时条码为空或空白。");
            return;
        }

        // 移除可能的起始引号或 '@' 符号
        if (barcode.StartsWith('@') || barcode.StartsWith('"'))
        {
            barcode = barcode[1..];
        }

        Log.Information("处理完成，最终处理条码: {Barcode}", barcode);

        // 更新UI显示的当前条码为最终处理的条码
        CurrentBarcode = barcode;

        // 请求View将焦点移回窗口
        RequestFocusToWindow?.Invoke();

        // 直接开始处理条码
        Task.Run(async () =>
        {
            await ProcessBarcodeAsync(barcode);
        });
    }

    #endregion
}