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
using DeviceService.DataSourceDevices.Camera.Models.Camera.Enums;
using History.Data;
using History.Views.Dialogs;
using Serilog;
using SharedUI.Models;
using Sunnen.Events;
using Sunnen.Models;
using Sunnen.Services;
using Sunnen.ViewModels.Settings;
using Color = System.Drawing.Color;
using Timer = System.Timers.Timer;
using System.Diagnostics;
using Camera.Models.Settings;
using DimensionImageSaveMode = Camera.Models.Settings.DimensionImageSaveMode;
using History.Configuration;
using Weight.Services;

namespace Sunnen.ViewModels.Windows;

/// <summary>
/// 主窗口视图模型
/// </summary>
public class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly IAudioService _audioService;
    private readonly ICameraService _cameraService;
    private readonly IDialogService _dialogService;
    private readonly SemaphoreSlim _measurementLock = new(1, 1);
    private readonly IPackageHistoryDataService _packageHistoryDataService;
    private readonly ISangNengService _sangNengService;
    private readonly ISettingsService _settingsService;
    private readonly Timer _timer;
    private readonly RenJiaCameraService _volumeCamera;
    private readonly IWeightService _weightService;
    private readonly INotificationService _notificationService;
    private ObservableCollection<SelectablePalletModel> _availablePallets;
    private string _currentBarcode = string.Empty;
    private ImageSource? _currentImage;
    private PackageInfo _currentPackage;
    private ObservableCollection<DeviceStatus> _deviceStatuses = [];
    private bool _disposed;
    private bool _hasPlayedErrorSound;
    private ObservableCollection<PackageInfo> _packageHistory = [];
    private ObservableCollection<PackageInfoItem> _packageInfoItems = [];
    private SelectablePalletModel? _selectedPallet;
    private ObservableCollection<StatisticsItem> _statisticsItems = [];
    private SystemStatus _systemStatus = SystemStatus.GetCurrentStatus();
    private ImageSource? _volumeImage;
    private string _lastProcessedBarcode = string.Empty;
    private DateTime _lastProcessedTime = DateTime.MinValue;
    private const int DuplicateBarcodeIntervalMs = 500; // 重复条码判断时间间隔（毫秒）
    private readonly SemaphoreSlim _barcodeProcessingLock = new(1, 1);

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

    private BitmapSource? _lastVolumeMeasuredImage; // 新增字段，用于暂存体积相机测量图像

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
            var stabilityCheckSamples = weightSettings.StabilityCheckSamples > 0 ? weightSettings.StabilityCheckSamples : 5;
            var stabilityThresholdGrams = weightSettings.StabilityThresholdGrams > 0 ? weightSettings.StabilityThresholdGrams : 20.0;
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

            // 使用最新的原始重量数据更新实时UI显示 (保持UI显示部分的即时性)
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (PackageInfoItems.Count <= 0) return; // 索引 0 是 "Weight"
                var weightDisplayItem = PackageInfoItems[0];
                // currentRawWeightKg 已经是 kg，直接格式化
                weightDisplayItem.Value = $"{currentRawWeightKg:F3}";
            });

            // 将当前原始重量(kg)添加到缓冲区
            _rawWeightBuffer.Add(currentRawWeightKg);
            if (_rawWeightBuffer.Count > stabilityCheckSamples)
            {
                _rawWeightBuffer.RemoveAt(0); // 保持缓冲区大小固定（滑动窗口）
            }

            // 如果缓冲区已满，则检查稳定性
            if (_rawWeightBuffer.Count != stabilityCheckSamples) return;
            double minWeightInWindow = _rawWeightBuffer.Min();
            double maxWeightInWindow = _rawWeightBuffer.Max();

            // 使用转换后的 stabilityThresholdKg 进行比较
            if (!((maxWeightInWindow - minWeightInWindow) < stabilityThresholdKg)) return;
            // 数据稳定
            // _rawWeightBuffer 中的数据已经是 kg，所以 Average() 也是 kg
            double stableAverageWeightKg = _rawWeightBuffer.Average();
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
            }
            Log.Debug("稳定的重量数据已添加到队列: {Weight}kg, 时间: {Timestamp}. 当前队列大小: {QueueSize}",
                stableEntry.WeightKg, stableEntry.Timestamp, _stableWeightQueue.Count);
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
                    UpdateImageDisplay(imageData, bitmap => { VolumeImage = bitmap; });
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
                    UpdateImageDisplay(imageData, bitmap => { CurrentImage = bitmap; });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理拍照相机图像流数据时发生错误");
                }
            });

        _availablePallets = [];

        SelectPalletCommand = new DelegateCommand<SelectablePalletModel>(ExecuteSelectPallet);

        // 订阅托盘设置更改事件
        _palletSettingsChangedSubscription = eventAggregator.GetEvent<PalletSettingsChangedEvent>().Subscribe(LoadAvailablePallets);

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
        Log.Debug("MainWindowViewModel 开始 Dispose..."); // 添加日志

        try
        {
            Log.Debug("MainWindowViewModel: 正在取消 Rx 订阅...");
            // 取消Rx订阅
            _weightDataSubscription?.Dispose();
            _volumeCameraImageSubscription?.Dispose();
            _cameraServiceImageSubscription?.Dispose();
            _palletSettingsChangedSubscription?.Dispose();
            Log.Debug("MainWindowViewModel: Rx 订阅已取消。");

            // 停止定时器
            _timer.Stop();
            _timer.Dispose();

            // 取消事件订阅
            _volumeCamera.ConnectionChanged -= OnVolumeCameraConnectionChanged;
            _cameraService.ConnectionChanged -= OnCameraConnectionChanged;
            _weightService.ConnectionChanged -= OnWeightScaleConnectionChanged;

            Log.Debug("MainWindowViewModel: 正在释放信号量...");
            // 释放信号量
            _measurementLock.Dispose();
            _barcodeProcessingLock.Dispose();
            Log.Debug("MainWindowViewModel: 信号量已释放。");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "释放资源时发生错误");
        }

        _disposed = true;
        Log.Debug("MainWindowViewModel Dispose 完成。"); // 添加日志
        GC.SuppressFinalize(this);
    }

    private static void UpdateImageDisplay(BitmapSource image, Action<BitmapSource> imageUpdater)
    {
        try
        {
            // 创建仅在需要更新时才会执行的延迟操作
            var updateOperation = new Action(() =>
            {
                try
                {
                    // 移除图像缩放逻辑
                    var width = image.Width;
                    var height = image.Height;

                    var stride = (int)(width * 4); // 假设为 Bgra32 格式
                    var pixelData = new byte[stride * (int)height];

                    // 不需要缩放，直接复制
                    image.CopyPixels(new Int32Rect(0, 0, (int)width, (int)height), pixelData, stride, 0);

                    // 在UI线程创建WriteableBitmap (使用原始尺寸)
                    var bitmap = new WriteableBitmap((int)width, (int)height, 96, 96, PixelFormats.Bgra32, null);
                    bitmap.WritePixels(new Int32Rect(0, 0, (int)width, (int)height), pixelData, stride, 0);
                    bitmap.Freeze(); // 使图像可以跨线程访问

                    imageUpdater(bitmap);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "在UI线程更新图像显示时发生错误");
                }
            });

            // 将操作排队到UI线程
            Application.Current.Dispatcher.Invoke(updateOperation);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新图像显示时发生错误");
        }
    }

    private void UpdateStatistics()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var items = StatisticsItems.ToList();

            // 更新总数
            items[0].Value = PackageHistory.Count.ToString();

            // 更新成功数
            var successCount = PackageHistory.Count(p => p.Status == PackageStatus.Success);
            items[1].Value = successCount.ToString();

            // 更新失败数
            var failedCount = PackageHistory.Count(p => p.Status == PackageStatus.Failed);
            items[2].Value = failedCount.ToString();

            // 计算处理速率（每小时）
            if (PackageHistory.Any())
            {
                var timeSpan = DateTime.Now - PackageHistory.Min(p => p.CreateTime);
                var ratePerHour = PackageHistory.Count / timeSpan.TotalHours;
                items[3].Value = $"{ratePerHour:F0}";
            }

            StatisticsItems = [.. items];
        });
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

        var currentBarcodeToProcess = barcode;

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            InitializePackageInfoItems();
            // 直接设置新条码用于UI显示
            CurrentBarcode = currentBarcodeToProcess;
            Log.Information("UI已更新条码: {Barcode}", currentBarcodeToProcess);
            // 在同一次调度中立即更新时间显示
            var items = PackageInfoItems.ToList();
            if (items.Count <= 2) return; // 确保项目已初始化且索引有效
            items[2].Value = DateTime.Now.ToString("HH:mm:ss"); // 更新时间显示
            PackageInfoItems = [.. items]; // 更新集合
        });

        try // 单个 try 块，管理信号量释放
        {
            // --- 原始逻辑开始 ---
            // 加载体积相机配置
            var cameraOverallSettings = _settingsService.LoadSettings<CameraOverallSettings>();
            Log.Information("等待处理包裹，延时: {TimeoutMs}毫秒", cameraOverallSettings.VolumeCamera.FusionTimeMs);

            // 等待配置的时间 - 此延迟在UI更新调度之后发生
            await Task.Delay(cameraOverallSettings.VolumeCamera.FusionTimeMs);

            Log.Information("延时结束，开始处理包裹: {Barcode}",
                currentBarcodeToProcess); // 使用此实例的正确条码进行日志记录

            try // 内部 try 块，处理包裹逻辑并捕获特定异常
            {
                // 防止短时间内重复处理相似条码 (检查包含关系)
                var now = DateTime.Now;
                var isDuplicate = false;
                if (!string.IsNullOrEmpty(_lastProcessedBarcode) &&
                    (now - _lastProcessedTime).TotalMilliseconds < DuplicateBarcodeIntervalMs)
                {
                    if ((_lastProcessedBarcode.Contains(currentBarcodeToProcess) &&
                         currentBarcodeToProcess.Length > 5) ||
                        (currentBarcodeToProcess.Contains(_lastProcessedBarcode) && _lastProcessedBarcode.Length > 5))
                    {
                        isDuplicate = true;
                    }
                }

                if (isDuplicate)
                {
                    Log.Warning("忽略相似条码：{Barcode}，上一条码：{LastBarcode}，间隔仅 {Interval}毫秒",
                        currentBarcodeToProcess, _lastProcessedBarcode, (now - _lastProcessedTime).TotalMilliseconds);
                    return; // 注意：此处直接返回，将执行外层 finally 块释放锁
                }

                // 更新最后处理的条码和时间
                _lastProcessedBarcode = currentBarcodeToProcess;
                _lastProcessedTime = now;

                // 重置错误音效播放标志
                _hasPlayedErrorSound = false;

                Log.Information("开始创建包裹对象: {Barcode}", currentBarcodeToProcess); // 使用一致的条码

                // 创建新的包裹对象
                _currentPackage = PackageInfo.Create();
                _currentPackage.SetBarcode(currentBarcodeToProcess); // 使用一致的条码
                _currentPackage.SetStatus(PackageStatus.Created);

                // 设置当前选中的托盘信息到包裹实例
                if (SelectedPallet != null)
                {
                    _currentPackage.SetPallet(
                        SelectedPallet.Name,
                        SelectedPallet.Weight,
                        SelectedPallet.Length,
                        SelectedPallet.Width,
                        SelectedPallet.Height);

                    Log.Information("设置包裹托盘信息：{PalletName}, 重量: {Weight}kg, 尺寸: {Length}×{Width}×{Height}cm",
                        SelectedPallet.Name, SelectedPallet.Weight,
                        SelectedPallet.Length, SelectedPallet.Width, SelectedPallet.Height);
                }

                var cts = new CancellationTokenSource();
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
                                            Log.Information("已在UI线程克隆并冻结图像");
                                        }
                                        catch (Exception uiEx)
                                        {
                                            Log.Error(uiEx, "在UI线程克隆/冻结图像时出错");
                                        }
                                    });
                                    if (frozenClone != null)
                                    {
                                        lock (imageLock)
                                        {
                                            capturedImage = frozenClone;
                                        }
                                        Log.Information("已从图像流获取并处理一帧图像");
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
                        var stableWeightQueryWindowSeconds = weightSettings.StableWeightQueryWindowSeconds > 0 ? weightSettings.StableWeightQueryWindowSeconds : 2;
                        
                        // IntegrationTimeMs 将用作如果初始查找失败后的最大等待时间
                        var maxWaitTimeForWeightMs = weightSettings.IntegrationTimeMs > 0 ? weightSettings.IntegrationTimeMs : 2000;

                        double? weightInKg = null;
                        // barcodeScanTime 代表包裹处理流程中，称重逻辑开始执行的时间点
                        var barcodeScanTime = DateTime.Now; 

                        StableWeightEntry? bestStableWeight;
                        // 1. 初始查找: 查找在 barcodeScanTime 之前的稳定重量
                        lock (_stableWeightQueue) 
                        {
                            bestStableWeight = _stableWeightQueue
                                .Where(entry => (barcodeScanTime - entry.Timestamp).TotalSeconds < stableWeightQueryWindowSeconds && 
                                                (barcodeScanTime - entry.Timestamp).TotalSeconds >= 0) 
                                .OrderByDescending(entry => entry.Timestamp)
                                .Cast<StableWeightEntry?>() 
                                .FirstOrDefault();
                        }
                        
                        if (bestStableWeight.HasValue)
                        {
                            weightInKg = bestStableWeight.Value.WeightKg;
                            Log.Information("包裹 {Barcode}: 使用初始查找的稳定重量: {Weight}kg (时间戳: {Timestamp})", 
                                _currentPackage.Barcode, // 使用当前包裹的条码
                                bestStableWeight.Value.WeightKg, 
                                bestStableWeight.Value.Timestamp);
                        }
                        else
                        {
                            // 2. 等待逻辑: 如果初始查找失败，则等待一段时间查找 barcodeScanTime 之后的重量
                            Log.Warning("包裹 {Barcode}: 初始查找在 {QueryWindow}s 内未能找到稳定重量 (处理时间: {ScanTime:HH:mm:ss.fff})。开始等待最多 {MaxWait}ms 查找后续重量。", 
                                        _currentPackage.Barcode, stableWeightQueryWindowSeconds, barcodeScanTime, maxWaitTimeForWeightMs);
                            
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
                                    Log.Information("包裹 {Barcode}: 等待期间获取到稳定重量: {Weight}kg (时间戳: {Timestamp:HH:mm:ss.fff})",
                                                    _currentPackage.Barcode, weightInKg, bestStableWeight.Value.Timestamp);
                                    break;
                                }
                                await Task.Delay(20, cts.Token); // 短暂延迟
                            }
                            sw.Stop();

                            if (!bestStableWeight.HasValue)
                            {
                                Log.Warning("包裹 {Barcode}: 在最大等待 {MaxWait}ms 后，仍未能获取到稳定重量。", _currentPackage.Barcode, maxWaitTimeForWeightMs);
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
                        await TriggerVolumeCamera(cts.Token);
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
                            Log.Information("根据配置获取尺寸刻度图，模式: {Mode}", volSettings.ImageSaveMode);
                            var dimensionImagesResult = await _volumeCamera.GetDimensionImagesAsync();

                            string barcodeSpecificPath = string.Empty; // 在外部声明以便后续使用
                            if (dimensionImagesResult.IsSuccess || volSettings.ImageSaveMode.HasFlag(DimensionImageSaveMode.Original))
                            {
                                // 即使 GetDimensionImagesAsync 失败，如果需要保存原图，仍需构建路径
                                var baseSavePath = volSettings.ImageSavePath;
                                var dateFolder = DateTime.Now.ToString("yyyy-MM-dd");
                                var sanitizedBarcode = string.Join("_",
                                    _currentPackage.Barcode.Split(Path.GetInvalidFileNameChars()));
                                barcodeSpecificPath = Path.Combine(baseSavePath, dateFolder, sanitizedBarcode);
                                Directory.CreateDirectory(barcodeSpecificPath); 
                            }

                            if (dimensionImagesResult.IsSuccess)
                            {
                                // 保存俯视图
                                if (volSettings.ImageSaveMode.HasFlag(DimensionImageSaveMode.Vertical) &&
                                    dimensionImagesResult.VerticalViewImage != null)
                                {
                                    const string fileName = "Vertical.jpg"; 
                                    var filePath = Path.Combine(barcodeSpecificPath, fileName);
                                    await SaveDimensionImageAsync(dimensionImagesResult.VerticalViewImage, filePath);
                                    Log.Information("已保存俯视图: {FilePath}", filePath);
                                }

                                // 保存侧视图
                                if (volSettings.ImageSaveMode.HasFlag(DimensionImageSaveMode.Side) &&
                                    dimensionImagesResult.SideViewImage != null)
                                {
                                    const string fileName = "Side.jpg"; 
                                    var filePath = Path.Combine(barcodeSpecificPath, fileName);
                                    await SaveDimensionImageAsync(dimensionImagesResult.SideViewImage, filePath);
                                    Log.Information("已保存侧视图: {FilePath}", filePath);
                                }
                                Log.Debug("已释放尺寸刻度图 BitmapSource 引用 (通过 dimensionImagesResult 作用域)");
                            }
                            else if(volSettings.ImageSaveMode.HasFlag(DimensionImageSaveMode.Vertical) || volSettings.ImageSaveMode.HasFlag(DimensionImageSaveMode.Side))
                            {
                                Log.Warning("获取尺寸刻度图失败: {Error}", dimensionImagesResult.ErrorMessage);
                            }

                            // 保存体积相机原图 (如果已配置)
                            if (volSettings.ImageSaveMode.HasFlag(DimensionImageSaveMode.Original))
                            {
                                if (_lastVolumeMeasuredImage != null)
                                {
                                    BitmapSource? frozenClone = null;
                                    await Application.Current.Dispatcher.InvokeAsync(() =>
                                    {
                                        try 
                                        { 
                                            frozenClone = _lastVolumeMeasuredImage.Clone(); 
                                            frozenClone.Freeze(); 
                                        }
                                        catch(Exception uiEx) { Log.Error(uiEx, "克隆/冻结体积相机测量原图时出错"); }
                                    });

                                    if (frozenClone != null)
                                    {
                                        if (string.IsNullOrEmpty(barcodeSpecificPath)) // 双重检查，理论上前面已创建
                                        {
                                            var baseSavePath = volSettings.ImageSavePath;
                                            var dateFolder = DateTime.Now.ToString("yyyy-MM-dd");
                                            var sanitizedBarcode = string.Join("_", _currentPackage.Barcode.Split(Path.GetInvalidFileNameChars()));
                                            barcodeSpecificPath = Path.Combine(baseSavePath, dateFolder, sanitizedBarcode);
                                            Directory.CreateDirectory(barcodeSpecificPath); 
                                        }
                                        const string fileName = "Original_Volume.jpg"; 
                                        var filePath = Path.Combine(barcodeSpecificPath, fileName);
                                        await SaveDimensionImageAsync(frozenClone, filePath); 
                                        Log.Information("已保存体积相机测量原图: {FilePath}", filePath);
                                    }
                                }
                                else
                                {
                                    Log.Warning("未能从测量过程中获取体积相机原图 (_lastVolumeMeasuredImage 为空)。");
                                }
                            }
                        }
                        else if (_currentPackage.Length <= 0) // 这个判断条件可能需要审视，原图保存可能不依赖于此
                        {
                            Log.Debug("包裹长度未获取或为0，跳过获取尺寸刻度图和体积相机原图的保存。");
                        }
                    }
                    catch (Exception imgEx)
                    {
                        Log.Error(imgEx, "获取或保存尺寸刻度图时发生错误");
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
                    var completionMessage = "Complete"; // UI String: 成功时的默认消息 (保持英文)

                    if (!isComplete)
                    {
                        Log.Warning("包裹信息不完整。条码缺失: {BarcodeMissing}, 重量缺失: {WeightMissing}, 体积缺失: {VolumeMissing}",
                            isBarcodeMissing, isWeightMissing, isVolumeMissing);

                        // 确定具体的错误消息 (UI Strings: 保持英文)
                        if (isBarcodeMissing) completionMessage = "Missing Barcode";
                        else if (isWeightMissing) completionMessage = "Missing Weight"; // 保持英文
                        else if (isVolumeMissing) completionMessage = "Missing Volume";
                        else completionMessage = "Data Incomplete"; // 后备，理论上不会发生

                        _currentPackage.SetStatus(PackageStatus.Failed, completionMessage);
                        PlayErrorSound();
                        // 移除立即更新UI的调用，保留音效播放
                    }
                    else
                    {
                        _currentPackage.SetStatus(PackageStatus.Success, completionMessage); // 使用默认的 "Complete" 消息
                        _ = _audioService.PlayPresetAsync(AudioType.Success); // 播放成功音效
                        // 移除立即更新UI的调用，保留音效播放
                    }

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
                                var cameraSettings = _settingsService.LoadSettings<CameraOverallSettings>("CameraSettings");
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
                    Log.Error(ex, "处理扫码信息时发生未预期的错误: {Barcode}", currentBarcodeToProcess); // 使用一致的条码
                    _currentPackage.SetStatus(PackageStatus.Error, "Processing Error"); // UI String
                    PlayErrorSound();
                }
            }
            catch (Exception ex) // 捕获包裹处理过程中的其他通用异常
            {
                Log.Error(ex, "处理扫码信息时发生未预期的错误: {Barcode}", currentBarcodeToProcess); // 使用一致的条码

                _currentPackage.SetStatus(PackageStatus.Error, "Processing Error"); // UI String
                PlayErrorSound();
            }
            // --- 原始逻辑结束 ---

            // 最后一次性更新所有UI：实时区域状态和历史记录（移到方法末尾）
            Application.Current.Dispatcher.Invoke(() =>
            {
                // 1. 先更新实时区域显示
                UpdateCurrentPackageUiDisplay(_currentPackage.Status, _currentPackage.StatusDisplay);

                // 2. 然后更新历史记录
                PackageHistory.Insert(0, _currentPackage);

                // 3. 最后更新统计信息
                UpdateStatistics();
            });
        }
        finally // 关联外层 Try，确保释放信号量
        {
            _barcodeProcessingLock.Release();
        }
    }

    private void PlayErrorSound()
    {
        if (_hasPlayedErrorSound) return;
        _hasPlayedErrorSound = true;
        _ = _audioService.PlayPresetAsync(AudioType.SystemError);
    }

    private async Task TriggerVolumeCamera(CancellationToken cancellationToken)
    {
        _lastVolumeMeasuredImage = null; // 重置暂存的图像
        try
        {
            // 使用信号量确保同一时间只有一个测量任务在进行
            if (!await _measurementLock.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken))
            {
                Log.Warning("上一次测量尚未完成，跳过本次测量");
                PlayErrorSound();
                return;
            }

            try
            {
                _currentPackage.SetStatus(PackageStatus.Created, "Measuring"); // UI String

                // 触发测量
                var result = await Task.Run(_volumeCamera.TriggerMeasure, cancellationToken);
                if (!result.IsSuccess)
                {
                    Log.Error("体积测量失败：{Error}", result.ErrorMessage);
                    _currentPackage.SetDimensions(0, 0, 0);
                    _currentPackage.SetStatus(PackageStatus.Failed, result.ErrorMessage); // ErrorMessage from service is likely English or technical
                    PlayErrorSound();
                    return;
                }
                _lastVolumeMeasuredImage = result.MeasuredImage; // <--- 暂存测量到的图像

                // 更新测量结果（将毫米转换为厘米）
                _currentPackage.SetDimensions(result.Length / 10.0, result.Width / 10.0, result.Height / 10.0);
                _currentPackage.SetStatus(PackageStatus.Success, "Success"); // UI String
            }
            finally
            {
                _measurementLock.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "体积测量过程发生错误");
            _currentPackage.SetStatus(PackageStatus.Error, ex.Message); // ex.Message is likely English or technical
            PlayErrorSound();
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
                    var dateFolder = DateTime.Now.ToString("yyyy-MM-dd");
                    var sanitizedBarcode = string.Join("_", package.Barcode.Split(Path.GetInvalidFileNameChars()));
                    
                    // 新的路径：基础路径/日期/条码
                    var barcodeSpecificPhotoPath = Path.Combine(basePhotoSavePath, dateFolder, sanitizedBarcode);
                    Directory.CreateDirectory(barcodeSpecificPhotoPath); // 确保条码级别目录存在

                    // 文件名可以简化，因为条码和日期已在路径中，或者保留时间戳以防万一（例如同一条码多次快照）
                    var photoFileName = $"Photo_{DateTime.Now:HHmmssfff}.jpg"; // Example: Photo_153055123.jpg
                    var finalFilePath = Path.Combine(barcodeSpecificPhotoPath, photoFileName);
                    imageName = photoFileName; // Update imageName to be just the file name for return value

                    // 先保存BitmapSource到临时文件 (临时文件可以在基础保存路径下，避免路径过长问题)
                    var tempFileName = $"temp_{sanitizedBarcode}_{DateTime.Now:yyyyMMddHHmmssfff}.jpg";
                    tempFilePath = Path.Combine(settings.ImageSave.SaveFolderPath, tempFileName); // Temp file in base directory

                    BitmapEncoder encoder = new JpegBitmapEncoder { QualityLevel = 90 };

                    encoder.Frames.Add(BitmapFrame.Create(image));

                    await using (var fileStream =
                                 new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        encoder.Save(fileStream);
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        Log.Warning("保存图像操作被取消 (编码后)");
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
                            $"Volume: {package.Volume / 1000.0:N0}cm³",
                            $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}"
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

                        // 转换为Base64 (从最终文件读取或从内存中的bitmap，这里选择从内存)
                        using var ms = new MemoryStream();
                        bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                        base64Image = Convert.ToBase64String(ms.ToArray());
                    }

                    // package.ImagePath = finalFilePath; // 访问性问题
                    Log.Information("已将主相机图像路径 {FilePath} 尝试设置到包裹对象", finalFilePath);

                    Log.Debug("主相机图像已保存：{FilePath}", finalFilePath);
                }
                catch (OperationCanceledException)
                {
                    Log.Warning("图像处理操作被取消 (SaveImageAsync Task.Run)");
                    throw; 
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "图像处理过程中发生错误 (SaveImageAsync Task.Run): {Error}", ex.Message);
                    throw;
                }

            }, cancellationToken);
             return (base64Image!, imageName); // imageName is now just the file name
        }
        catch (OperationCanceledException)
        {
            Log.Warning("保存图像任务被取消");
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
    private void UpdateCurrentPackageUiDisplay(PackageStatus status, string customDisplay)
    {
        var items = PackageInfoItems.ToList();
        // 更新尺寸 (UI String from PackageInfoItem definition)
        items[1].Value = $"{_currentPackage.Length:F1}x{_currentPackage.Width:F1}x{_currentPackage.Height:F1}";
        // 更新时间 (UI String from PackageInfoItem definition)
        items[2].Value = DateTime.Now.ToString("HH:mm:ss");
        // 更新状态
        var statusItem = items[3];
        string displayText;
        string color;

        switch (status)
        {
            case PackageStatus.Success:
                displayText = "Success"; // UI String
                color = "#4CAF50"; // Green
                break;
            case PackageStatus.Failed:
                // 使用自定义显示（如果可用），否则为 "Failed" (UI Strings)
                displayText = !string.IsNullOrEmpty(customDisplay) && customDisplay != "Complete"
                    ? customDisplay 
                    : "Failed";
                color = "#FF0000"; // Red
                break;
            case PackageStatus.Error:
                displayText = !string.IsNullOrEmpty(customDisplay) ? customDisplay : "Error"; // UI String
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
            var volumeCameraStatus = DeviceStatuses.FirstOrDefault(x => x.Name == "Volume Camera"); // UI String
            if (volumeCameraStatus == null) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                volumeCameraStatus.Status = isConnected ? "Online" : "Offline"; // UI Strings
                volumeCameraStatus.StatusColor = isConnected ? "#4CAF50" : "#FFA500";
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
            var cameraStatus = DeviceStatuses.FirstOrDefault(x => x.Name == "Photo Camera"); // UI String
            if (cameraStatus == null) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                cameraStatus.Status = isConnected ? "Online" : "Offline"; // UI Strings
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
            var weightScaleStatus = DeviceStatuses.FirstOrDefault(x => x.Name == "Weight Scale"); // UI String
            if (weightScaleStatus == null) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                weightScaleStatus.Status = isConnected ? "Online" : "Offline"; // UI Strings
                weightScaleStatus.StatusColor = isConnected ? "#4CAF50" : "#FFA500";
            });

            // 如果断开连接，清除最新的重量数据和队列
            if (isConnected) return;
            _rawWeightBuffer.Clear();
            lock (_stableWeightQueue) // 确保线程安全
            {
                _stableWeightQueue.Clear();
            }
            Log.Information("重量称已断开连接，清除原始重量缓冲和稳定重量队列。");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新重量称状态时发生错误");
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

    public ICommand OpenSettingsCommand { get; }

    /// <summary>
    /// 打开历史记录窗口命令
    /// </summary>
    public ICommand OpenHistoryWindowCommand { get; }

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

    public DelegateCommand<SelectablePalletModel> SelectPalletCommand { get; }

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
                Log.Warning("设置托盘高度失败：代码 {Result}", result);
                // Notification strings are UI and kept in English
                _notificationService.ShowWarning(
                    $"Failed to set pallet height for '{pallet.Name}' (Error code: {result}). Please try selecting the pallet again.");
            }
            else
            {
                Log.Information("成功设置托盘高度：{Height}mm", palletHeightMm);
                _notificationService.ShowSuccess(
                    $"Pallet '{pallet.Name}' with height {pallet.Height:F1}cm selected successfully");
            }

            // 保存选择的托盘名称到配置
            var mainWindowSettings = _settingsService.LoadSettings<MainWindowSettings>();
            mainWindowSettings.LastSelectedPalletName = pallet.Name;
            _settingsService.SaveSettings(mainWindowSettings);
            Log.Information("已保存用户选择的托盘：{PalletName}", pallet.Name);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "选择或设置托盘高度时发生错误");
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
            var lastSelectedPalletName = mainWindowSettings.LastSelectedPalletName ?? "noPallet"; // "noPallet" is a key/identifier

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
                        Log.Warning("启动时设置上次托盘高度失败：代码 {Result}", result);
                        // 此处可以选择是否通知用户，或者只记录日志
                    }
                    else
                    {
                        Log.Information("启动时成功设置上次托盘高度：{Height}mm", palletHeightMm);
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
                Log.Warning("上次保存的托盘 '{LastPalletName}' 未找到，已默认选择空托盘", lastSelectedPalletName);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载托盘配置或应用上次选择时失败");
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
        Log.Debug("检查并更新初始设备状态...");
        try
        {
            // 更新拍照相机状态
            OnCameraConnectionChanged(_cameraService.GetType().Name, _cameraService.IsConnected);

            // 更新体积相机状态
            OnVolumeCameraConnectionChanged(_volumeCamera.GetType().Name, _volumeCamera.IsConnected);

            // 更新重量称状态
            OnWeightScaleConnectionChanged(_weightService.GetType().Name, _weightService.IsConnected);

            Log.Debug("初始设备状态更新完成。");
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
    /// <param name="format">所需的图像格式（默认为 Jpeg）。</param>
    /// <returns>表示异步操作的任务。</returns>
    private static async Task SaveDimensionImageAsync(BitmapSource image, string filePath,
        ImageFormat format = ImageFormat.Jpeg)
    {
        try
        {
            await Task.Run(() =>
            {
                BitmapEncoder encoder = format switch
                {
                    ImageFormat.Jpeg => new JpegBitmapEncoder { QualityLevel = 90 },
                    ImageFormat.Png => new PngBitmapEncoder(),
                    ImageFormat.Bmp => new BmpBitmapEncoder(),
                    ImageFormat.Tiff => new TiffBitmapEncoder(),
                    _ => new JpegBitmapEncoder { QualityLevel = 90 }
                };

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

    private HistoryViewConfiguration CreateSangNengHistoryViewConfiguration()
    {
        return new HistoryViewConfiguration
        {
            ColumnSpecs =
            [
                new() { PropertyName = nameof(PackageHistoryRecord.Index), HeaderResourceKey = "PackageHistory_Header_Index", IsDisplayed = true, DisplayOrderInGrid = 0, Width = "Auto" },
                new() { PropertyName = nameof(PackageHistoryRecord.Barcode), HeaderResourceKey = "PackageHistory_Header_Barcode", IsDisplayed = true, DisplayOrderInGrid = 1, Width = "*" },
                new() { PropertyName = "ImageAction", HeaderResourceKey = "PackageHistory_Header_ImageAction", IsDisplayed = true, DisplayOrderInGrid = 2, Width = "Auto", IsTemplateColumn = true },
                new() { PropertyName = nameof(PackageHistoryRecord.Weight), HeaderResourceKey = "PackageHistory_Header_Weight", IsDisplayed = true, DisplayOrderInGrid = 3, Width = "*", StringFormat = "F3" },
                new() { PropertyName = nameof(PackageHistoryRecord.Length), HeaderResourceKey = "PackageHistory_Header_Length", IsDisplayed = true, DisplayOrderInGrid = 4, Width = "*", StringFormat = "F1" },
                new() { PropertyName = nameof(PackageHistoryRecord.Width), HeaderResourceKey = "PackageHistory_Header_Width", IsDisplayed = true, DisplayOrderInGrid = 5, Width = "*", StringFormat = "F1" },
                new() { PropertyName = nameof(PackageHistoryRecord.Height), HeaderResourceKey = "PackageHistory_Header_Height", IsDisplayed = true, DisplayOrderInGrid = 6, Width = "*", StringFormat = "F1" },
                new() { PropertyName = nameof(PackageHistoryRecord.CreateTime), HeaderResourceKey = "PackageHistory_Header_CreateTime", IsDisplayed = true, DisplayOrderInGrid = 7, Width = "*", StringFormat = "yyyy-MM-dd HH:mm:ss" },
                new() { PropertyName = nameof(PackageHistoryRecord.PalletName), HeaderResourceKey = "PackageHistory_Header_PalletName", IsDisplayed = true, DisplayOrderInGrid = 8 },
                new() { PropertyName = nameof(PackageHistoryRecord.PalletWeight), HeaderResourceKey = "PackageHistory_Header_PalletWeight", IsDisplayed = true, DisplayOrderInGrid = 9, StringFormat = "F3"  },
                new() { PropertyName = nameof(PackageHistoryRecord.Status), HeaderResourceKey = "PackageHistory_Header_Status", IsDisplayed = true, DisplayOrderInGrid = 10 }, // StatusDisplay might be preferred by ViewModel
                new() { PropertyName = nameof(PackageHistoryRecord.ChuteNumber), HeaderResourceKey = "PackageHistory_Header_ChuteNumber", IsDisplayed = false, DisplayOrderInGrid = 11 }, // Example: Hide ChuteNumber for SangNeng
                new() { PropertyName = nameof(PackageHistoryRecord.ErrorMessage), HeaderResourceKey = "PackageHistory_Header_Error", IsDisplayed = true, DisplayOrderInGrid = 12 },

                // Ensure all other relevant fields from default that should be exportable but not displayed are still in the list with IsDisplayed = false, IsExported = true
                // Or, rely on the ViewModel's default spec for export if a property is not in this custom grid spec.
                // For simplicity, here we only define what's displayed in the grid. Export behavior will use these + any defaults for non-listed props.
                 new() { PropertyName = nameof(PackageHistoryRecord.ImagePath), HeaderResourceKey = "PackageHistory_Header_ImagePath", IsDisplayed = false, IsExported = true } // Export path, but button is used for view
            ]
        };
    }

    #endregion
}