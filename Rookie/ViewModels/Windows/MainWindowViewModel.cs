using System.Collections.ObjectModel;
using System.IO;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Camera.Interface;
using Camera.Services;
using Camera.Services.Implementations.Hikvision.Security;
using Camera.Services.Implementations.Hikvision.Volume;
using Common.Models.Package;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.Camera.Models.Camera;
using Rookie.Models.Api;
using Rookie.Services;
using Serilog;
using SharedUI.Models;
using Sorting_Car.Models;
using Sorting_Car.Services;
using Weight.Services;
using LocalWeightSettings = Weight.Models.Settings.WeightSettings;

namespace Rookie.ViewModels.Windows;

public class MainWindowViewModel : BindableBase, IDisposable
{
    private static long _globalPackageIndex;
    private static int _nextCyclicChute = 0; // 新增: 用于循环格口分配的静态计数器

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

    private const int SupplementDataPollIntervalMs = 50;
    private const int MIN_SAMPLES_FOR_STABILITY_CHECK = 2;

    private (double WeightInKg, DateTime Timestamp)? _lastReceivedValidWeight;
    private volatile Tuple<double, DateTime>? _logicOrientedWeightCache;
    private (float Length, float Width, float Height, DateTime Timestamp)? _lastReceivedValidVolume;

    private readonly HikvisionSecurityCameraService _securityCameraService;
    private readonly HikvisionVolumeCameraService _volumeCameraService;
    private readonly ICameraService _industrialCameraService;
    private readonly IWeightService _weightService;
    private readonly CarSortService _carSortService;
    private readonly CarSortingService _carSortingService;

    private volatile bool _isProcessingPackage;

    // Helper function for weight stability check
    private static bool IsWeightStable(List<double> weightValues, double tolerance, out double stableWeight)
    {
        stableWeight = 0;
        if (weightValues.Count < MIN_SAMPLES_FOR_STABILITY_CHECK)
        {
            if (weightValues.Count == 1 && MIN_SAMPLES_FOR_STABILITY_CHECK == 1)
            {
                 stableWeight = weightValues[0];
                 return true;
            }
            return false;
        }

        double min = weightValues.Min();
        double max = weightValues.Max();

        if ((max - min) <= tolerance)
        {
            stableWeight = weightValues.Average(); 
            return true;
        }
        return false;
    }

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

        // 订阅体积数据流
        _subscriptions.Add(_volumeCameraService.VolumeDataWithVerticesStream
            .Subscribe(volumeData =>
            {
                Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
                {
                    var (length, width, height, timestamp, isValid, receivedImage) = volumeData;
                    try
                    {
                        var dimensionsItem = PackageInfoItems.FirstOrDefault(i => i.Label == "Dimensions");
                        if (dimensionsItem != null)
                        {
                            if (isValid && length > 0 && width > 0 && height > 0)
                            {
                                dimensionsItem.Value = $"{length:F0}x{width:F0}x{height:F0}";
                                _lastReceivedValidVolume = (length, width, height, timestamp);
                            }
                            else
                            {
                                dimensionsItem.Value = "-- x -- x --";
                            }
                        }
                        CurrentImage = receivedImage;

                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "更新实时包裹体积信息或主图像时出错。");
                    }
                });
            }, ex => Log.Error(ex, "体积数据流订阅出错。")));

        // 订阅重量数据流
        _subscriptions.Add(_weightService.WeightDataStream
            .Subscribe(weightData =>
            {
                Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
                {
                    try
                    {
                        var weightItem = PackageInfoItems.FirstOrDefault(i => i.Label == "Weight");
                        if (weightItem == null) return;
                        if (weightData != null)
                        {
                            // 将 double 格式化为字符串，保留两位小数
                            weightItem.Value = weightData.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                            // 更新 _lastReceivedValidWeight 缓存
                            if (weightData.Value > 0) // 假设有效重量是正数
                            {
                                _lastReceivedValidWeight = (weightData.Value, weightData.Timestamp);
                            }
                        }
                        else
                        {
                            weightItem.Value = "0.00"; 
                           
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "更新实时包裹重量信息时出错。");
                    }
                });
            }, ex => Log.Error(ex, "重量数据流订阅出错。")));

        // 新增: 专门为处理逻辑订阅重量数据流，在后台线程更新 _logicOrientedWeightCache
        _subscriptions.Add(_weightService.WeightDataStream
            .ObserveOn(TaskPoolScheduler.Default)
            .Where(weightData => weightData != null && weightData.Value > 0)
            .Subscribe(weightData =>
            {
                _logicOrientedWeightCache = new Tuple<double, DateTime>(weightData!.Value, weightData.Timestamp);
            }, ex => Log.Error(ex, "后台逻辑重量数据流订阅出错。")));

        cameraDataProcessingService.PackageStream.Subscribe(async void (packageInfo) => await ProcessPackageAsync(packageInfo));
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
        _dialogService.ShowDialog("HistoryDialogView", new DialogParameters(), _ => { });
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
                Status = "Disconnected",
                Icon = "VideoSecurity24",
                StatusColor = "#F44336"
            });
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "Hikvision Volume",
                Status = "Disconnected",
                Icon = "CubeScan24",
                StatusColor = "#F44336"
            });
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "Hikvision Industrial",
                Status = "Disconnected",
                Icon = "Camera24",
                StatusColor = "#F44336"
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
                var weightScaleStatus = DeviceStatuses.FirstOrDefault(d => d.Name == "Scales");
                if (weightScaleStatus == null) return;
                weightScaleStatus.Status = isConnected ? "Connected" : "Disconnected";
                weightScaleStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
                Log.Information("称重模块连接状态已更新: {Status}", weightScaleStatus.Status);
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
        try
        {
            Log.Information("开始处理包裹: {Barcode}, 触发时间: {CreateTime:HH:mm:ss.fff}", package.Barcode, package.CreateTime);
            if (package.Weight <= 0)
            {
                var weightSettings = _settingsService.LoadSettings<LocalWeightSettings>();
                var stableSamplesRequired = Math.Max(MIN_SAMPLES_FOR_STABILITY_CHECK, weightSettings.StableWeightSamples);
                var fusionTimeMs = weightSettings.IntegrationTimeMs; 
                var stabilityToleranceKg = 0.02; // 20g tolerance, can be made a setting if needed

                Log.Information("包裹 {Barcode}: 重量为0或无效，尝试获取稳定重量。稳定样本数配置: {ConfigSamples}, 实际最少判断样本数: {ActualSamples}, 融合/轮询时间: {FusionMs}ms, 公差: {ToleranceKg}kg",
                                package.Barcode, weightSettings.StableWeightSamples, stableSamplesRequired, fusionTimeMs, stabilityToleranceKg);

                List<(double weight, DateTime timestamp)> collectedWeightEntries = new();
                DateTime packageCreateTime = package.CreateTime;
                bool weightSupplemented = false;
                int collectedEntriesCountForLog = 0;

                for (int i = 0; i < (fusionTimeMs / SupplementDataPollIntervalMs); i++)
                {
                    await Task.Delay(SupplementDataPollIntervalMs);

                    var currentLogicalWeightTuple = _logicOrientedWeightCache;

                    if (currentLogicalWeightTuple != null)
                    {
                        var currentWeightVal = currentLogicalWeightTuple.Item1;
                        var tsCurrentWeight = currentLogicalWeightTuple.Item2;
                        
                        if (Math.Abs((tsCurrentWeight - packageCreateTime).TotalMilliseconds) <= fusionTimeMs)
                        {
                            if (!collectedWeightEntries.Any() || collectedWeightEntries.Last().timestamp < tsCurrentWeight)
                            {
                                collectedWeightEntries.Add((currentWeightVal, tsCurrentWeight));
                                collectedEntriesCountForLog = collectedWeightEntries.Count;
                                Log.Verbose("包裹 {Barcode}: 轮询中收集到重量样本 {Weight}kg @ {Timestamp:HH:mm:ss.fff}. 已收集 {Count} 个不同时间戳的条目.",
                                           package.Barcode, currentWeightVal, tsCurrentWeight, collectedEntriesCountForLog);

                                if (collectedEntriesCountForLog >= stableSamplesRequired)
                                {
                                    var samplesForCheck = collectedWeightEntries
                                                            .Skip(Math.Max(0, collectedEntriesCountForLog - stableSamplesRequired))
                                                            .Select(entry => entry.weight)
                                                            .ToList();
                                    
                                    if (IsWeightStable(samplesForCheck, stabilityToleranceKg, out double stableWeightValue))
                                    {
                                        package.SetWeight(stableWeightValue);
                                        Log.Information("包裹 {Barcode}: 成功获取稳定重量: {Weight}kg (基于最近 {NumSamples} 个样本，轮询尝试: {Attempt})",
                                                        package.Barcode, package.Weight, samplesForCheck.Count, i + 1);
                                        weightSupplemented = true;
                                        break; 
                                    }
                                    Log.Verbose("包裹 {Barcode}: 已收集 {NumCollected} 个样本, 最近 {NumChecked} 个样本尚不稳定 (轮询尝试 {Attempt})。样本: [{SampleData}]", 
                                                package.Barcode, collectedEntriesCountForLog, samplesForCheck.Count, i + 1, string.Join(", ", samplesForCheck));
                                }
                            }
                        }
                    }
                }

                if (!weightSupplemented)
                {
                    Log.Warning("包裹 {Barcode}: 轮询等待 {Timeout}ms后 ({Attempts}次尝试)，未能获取到稳定重量。共收集到 {CollectedCount} 个不同时间戳的重量条目。", 
                                package.Barcode, fusionTimeMs, fusionTimeMs / SupplementDataPollIntervalMs, collectedEntriesCountForLog);
                }
            }

            // 修改后的条件，用于检查尺寸是否为null或无效
            if (!package.Length.HasValue || package.Length.Value <= 0 || 
                !package.Width.HasValue || package.Width.Value <= 0 || 
                !package.Height.HasValue || package.Height.Value <= 0)
            {
                // 按需加载相机设置
                var cameraSettings = _settingsService.LoadSettings<CameraSettings>();
                var fusionTimeMs = cameraSettings.VolumeCameraFusionTimeMs; // 作为融合时间窗口

                bool volumeSupplementedSuccessfully = false;

                bool TrySupplementVolumeFromStream()
                {
                    if (_lastReceivedValidVolume.HasValue)
                    {
                        var (l, w, h, tsVolume) = _lastReceivedValidVolume.Value;
                        if (Math.Abs((tsVolume - package.CreateTime).TotalMilliseconds) <= fusionTimeMs)
                        {
                            package.SetDimensions(l, w, h);
                            Log.Information("包裹 {Barcode}: 从缓存的有效流数据补充尺寸: 长{L} 宽{W} 高{H} (时间戳匹配). 体积时间: {tsVolume:HH:mm:ss.fff}, 包裹创建时间: {pkgCreateTime:HH:mm:ss.fff}",
                                package.Barcode, package.Length, package.Width, package.Height, tsVolume, package.CreateTime);
                            return true;
                        }
                        Log.Information("包裹 {Barcode}: 缓存的体积数据时间戳 ({tsVolume:HH:mm:ss.fff}) 与包裹创建时间 ({pkgCreateTime:HH:mm:ss.fff}) 相差超过 {FusionMs}ms，视为陈旧数据。",
                                package.Barcode, tsVolume, package.CreateTime, fusionTimeMs);
                    }
                    return false;
                }

                if (TrySupplementVolumeFromStream())
                {
                }
                else
                {
                    Log.Information("包裹 {Barcode}: 从流数据缓存补充尺寸失败，尝试主动获取一次测量结果。", package.Barcode);
                    var (isSdkCallSuccess, l, w, h, isMeasurementValid, _, _, errorMsg) = _volumeCameraService.GetSingleMeasurement();

                    if (isSdkCallSuccess && isMeasurementValid && l > 0 && w > 0 && h > 0)
                    {
                        package.SetDimensions(l, w, h);
                        Log.Information("包裹 {Barcode}: 单次主动获取成功补充尺寸: 长{L} 宽{W} 高{H}", package.Barcode, l, w, h);
                    }
                    else
                    {
                        Log.Warning("包裹 {Barcode}: 单次主动获取尺寸失败或数据无效/为零。SDK调用成功: {SdkSuccess}, 测量有效: {MeasureValid}, LWH: {L},{W},{H}. 错误: {ErrorMsg}。尝试轮询主动获取。",
                                    package.Barcode, isSdkCallSuccess, isMeasurementValid, l, w, h, errorMsg ?? "N/A");

                        const int maxAttempts = 10;
                        const int delayPerAttemptMs = 10;
                        for (int attempt = 0; attempt < maxAttempts; attempt++)
                        {
                            await Task.Delay(delayPerAttemptMs);
                            Log.Information("包裹 {Barcode}: 轮询主动获取尺寸，尝试次数: {Attempt}/{MaxAttempts}", package.Barcode, attempt + 1, maxAttempts);
                            var (pollSdkSuccess, pollL, pollW, pollH, pollValid, _, _, pollErrorMsg) = _volumeCameraService.GetSingleMeasurement();

                            if (pollSdkSuccess && pollValid && pollL > 0 && pollW > 0 && pollH > 0)
                            {
                                package.SetDimensions(pollL, pollW, pollH);
                                Log.Information("包裹 {Barcode}: 轮询主动获取成功补充尺寸: 长{L} 宽{W} 高{H} (尝试 {Attempt})", package.Barcode, pollL, pollW, pollH, attempt + 1);
                                volumeSupplementedSuccessfully = true;
                                break;
                            }
                            Log.Debug("包裹 {Barcode}: 轮询主动获取尝试 {Attempt} 失败。SDK调用成功: {SdkSuccess}, 测量有效: {MeasureValid}, LWH: {L},{W},{H}. 错误: {ErrorMsg}",
                                       package.Barcode, attempt + 1, pollSdkSuccess, pollValid, pollL, pollW, pollH, pollErrorMsg ?? "N/A");
                        }

                        if (!volumeSupplementedSuccessfully)
                        {
                            Log.Warning("包裹 {Barcode}: 经过流缓存、单次主动获取和 {MaxAttempts} 次轮询主动获取后，仍未能补充有效尺寸。", package.Barcode, maxAttempts);
                        }
                    }
                }
            }

            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                package.Index = (int)Interlocked.Increment(ref _globalPackageIndex);
                CurrentBarcode = package.Barcode;
                UpdatePackageInfoItems(package);
                Interlocked.Increment(ref _totalPackageCount);
            });

            // 检查重量和尺寸是否有效，无效则直接失败
            bool hasWeight = package.Weight > 0;
            bool hasDimensions = package is { Length: > 0, Width: > 0, Height: > 0 };

            if (!hasWeight || !hasDimensions)
            {
                string uiErrorMessage = hasWeight switch
                {
                    false when !hasDimensions => "Package weight and volume information are missing",
                    false => "Package weight information is missing",
                    _ => "Package volume information is missing"
                };
                string logMessage = hasWeight switch // 中文日志信息
                {
                    false when !hasDimensions => "包裹重量和体积信息均缺失",
                    false => "包裹重量信息缺失",
                    _ => "包裹体积信息缺失"
                };

                Log.Warning("包裹 {Barcode}: {Reason}，跳过DCS及后续流程。", package.Barcode, logMessage);
                var combinedErrorMessageForUi = string.IsNullOrEmpty(package.ErrorMessage) // ErrorMessage 可能已有内容
                    ? uiErrorMessage
                    : $"{package.ErrorMessage}; {uiErrorMessage}"; // 假设 package.ErrorMessage 已是英文或无关紧要
                package.SetStatus(PackageStatus.Failed, combinedErrorMessageForUi); // UI相关的错误信息使用英文
            }
            else // 重量和尺寸有效，开始DCS流程
            {
                // 1. 调用安防相机抓图并上传
                BitmapSource? capturedImage = _securityCameraService.CaptureAndGetBitmapSource();
                if (capturedImage != null)
                {
                    string tempImageDirectory = Path.Combine(Path.GetTempPath(), "RookieParcelImages");
                    string tempImageFileName = $"pkg_{package.Index}_{DateTime.UtcNow:yyyyMMddHHmmssfff}.jpg";
                    string? tempImagePath = SaveBitmapSourceAsJpeg(capturedImage, tempImageDirectory, tempImageFileName);

                    if (tempImagePath != null)
                    {
                        Log.Information("包裹 {Barcode}: 安防相机图片已保存到临时路径 {Path}", package.Barcode, tempImagePath);
                        string? imageUrl = await _rookieApiService.UploadImageAsync(tempImagePath);
                        if (!string.IsNullOrEmpty(imageUrl))
                        {
                            package.ImagePath = imageUrl;
                            Log.Information("包裹 {Barcode}: 图片上传到DCS成功: {ImageUrl}", package.Barcode, imageUrl);
                        }
                        else
                        {
                            Log.Warning("包裹 {Barcode}: 图片上传到DCS失败。", package.Barcode);
                            package.ImagePath = null;
                        }
                        try
                        {
                            File.Delete(tempImagePath);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "删除临时图片文件失败: {Path}", tempImagePath);
                        }
                    }
                    else
                    {
                        Log.Warning("包裹 {Barcode}: 保存安防相机抓图到临时文件失败。", package.Barcode);
                        package.ImagePath = null;
                    }
                }
                else
                {
                    Log.Information("包裹 {Barcode}: 未从安防相机抓取到图片，跳过图片上传。", package.Barcode);
                    package.ImagePath = null;
                }

                // 2. 调用 sorter.parcel_info_upload
                Log.Information("包裹 {Barcode}: 准备上传包裹基础信息到DCS。图片路径: {ImagePath}", package.Barcode, package.ImagePath ?? "无");
                bool parcelInfoUploaded = await _rookieApiService.UploadParcelInfoAsync(package);
                if (!parcelInfoUploaded)
                {
                    package.SetStatus(PackageStatus.Failed, "Parcel info upload to DCS failed"); // UI 英文
                    Log.Error("包裹 {Barcode}: 包裹基础信息上传DCS失败。", package.Barcode);
                }
                else
                {
                    Log.Information("包裹 {Barcode}: 包裹基础信息上传DCS成功。", package.Barcode);
                    // 3. 调用 sorter.dest_request (仅当 parcel_info_upload 成功)
                    DestRequestResultParams? destResult = await _rookieApiService.RequestDestinationAsync(package.Barcode);
                    if (destResult?.ErrorCode == 0 && int.TryParse(destResult.ChuteCode, out int apiChute) && apiChute > 0)
                    {
                        // 新增: 循环分配格口号 1-4
                        if (_nextCyclicChute <= 0 || _nextCyclicChute >= 4) // 如果是初始值或达到上限，则重置为1开始
                        {
                            _nextCyclicChute = 1;
                        }
                        else
                        {
                            _nextCyclicChute++;
                        }
                        package.SetChute(_nextCyclicChute);
                        Log.Information("包裹 {Barcode}: DCS请求目的地成功。已覆盖API分配格口，循环分配格口为: {CyclicChute}", package.Barcode, _nextCyclicChute);
                        
                        package.SetStatus(PackageStatus.Success); 

                        Log.Information("包裹 {Barcode}: 准备调用小车分拣服务，目标格口(循环分配后): {Chute}", package.Barcode, package.ChuteNumber);
                        var carSortResult = await _carSortService.ProcessPackageSortingAsync(package);
                        if (carSortResult)
                        {
                            Log.Information("包裹 {Barcode}: 小车分拣服务处理成功。", package.Barcode);
                            // 如果之前是Success, 保持Success。如果ProcessPackageSortingAsync内部修改了状态(例如错误)，以那个为准
                        }
                        else
                        {
                            Log.Warning("包裹 {Barcode}: 小车分拣服务处理失败。", package.Barcode);
                            const string carSortErrorUi = "Car sorting failed"; // UI 英文
                            var currentErrorMessage = string.IsNullOrEmpty(package.ErrorMessage) || package.Status == PackageStatus.Success // 如果之前是Success, ErrorMessage为空
                                ? carSortErrorUi 
                                : $"{package.ErrorMessage}; {carSortErrorUi}"; // 假设 package.ErrorMessage 已是英文或其内容适合拼接
                            package.SetStatus(PackageStatus.Error, currentErrorMessage); // UI 英文
                        }
                    }
                    else
                    {
                        string destErrorUi = destResult == null
                            ? "DCS destination request API call failed"
                            : $"DCS destination request logical error: Code {destResult.ErrorCode}, Chute '{destResult.ChuteCode}'"; // UI 英文
                        string destErrorLog = destResult == null // 中文日志
                            ? "DCS请求目的地API调用失败"
                            : $"DCS请求目的地逻辑错误: 代码 {destResult.ErrorCode}, 格口 '{destResult.ChuteCode}'";
                        package.SetStatus(PackageStatus.Failed, destErrorUi);
                        Log.Error("包裹 {Barcode}: {Error}", package.Barcode, destErrorLog);
                    }
                }
            }

            string chuteToReport;
            if (package.ChuteNumber > 0)
            {
                chuteToReport = package.ChuteNumber.ToString();
            }
            else
            {
                chuteToReport = _carSequenceSettings.ExceptionChuteNumber > 0 
                                ? _carSequenceSettings.ExceptionChuteNumber.ToString() 
                                : "N/A"; // Fallback if ExceptionChuteNumber is somehow invalid
                Log.Information("包裹 {Barcode}: 未分配有效格口或处理失败，将上报至配置的异常口/默认值: {ErrorChuteValue}", package.Barcode, chuteToReport);
            }

            bool successForApiReport = package.Status == PackageStatus.Success;
            string? errorReasonForApiReportUi = successForApiReport
                ? null
                : (string.IsNullOrEmpty(package.ErrorMessage) ? "Unknown sorting error" : package.ErrorMessage); // ErrorMessage已是英文
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
            _isProcessingPackage = false;
            // StatusDisplay 假设由 PackageInfo 控制，可能是中文或已本地化
            Log.Information("包裹 {Barcode} 处理流程结束 (或者被忽略后标志位重置)。最终状态: {StatusDisplay}", package.Barcode, package.StatusDisplay);
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
            if (package.Length > 0 || package.Width > 0 || package.Height > 0)
            {
                dimensionsItem.Value = $"{package.Length:F0}x{package.Width:F0}x{package.Height:F0}";
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
}