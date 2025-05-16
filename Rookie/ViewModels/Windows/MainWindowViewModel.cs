using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Common.Models.Package;
using Serilog;
using SharedUI.Models;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Rookie.Services;
using DeviceService.DataSourceDevices.Weight;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.Camera.Models.Camera;
using System.IO;
using Camera.Interface;
using Camera.Services;
using Camera.Services.Implementations.Hikvision.Security;
using Camera.Services.Implementations.Hikvision.Volume;
using Rookie.Models.Api;
using Sorting_Car.Services;
using Weight.Services;

namespace Rookie.ViewModels.Windows;

public class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly IDialogService _dialogService;
    private readonly IRookieApiService _rookieApiService;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly DispatcherTimer _timer;
    private readonly ISettingsService _settingsService;

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

    private (double WeightInGrams, DateTime Timestamp)? _lastReceivedValidWeight;
    private (float Length, float Width, float Height, DateTime Timestamp)? _lastReceivedValidVolume;

    private readonly HikvisionSecurityCameraService _securityCameraService;
    private readonly HikvisionVolumeCameraService _volumeCameraService;
    private readonly ICameraService _industrialCameraService;
    private readonly IWeightService _weightService;
    private readonly CarSortService _carSortService;
    private readonly CarSortingService _carSortingService;

    private volatile bool _isProcessingPackage;

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
                            Log.Error(ex, "Error updating EventPackageImage on UI thread from Industrial  live stream.");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error processing image from Industrial  live stream.");
                }
            }, ex => Log.Error(ex, "Error in Industrial  live image stream subscription.")));

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
                        var weightItem = PackageInfoItems.FirstOrDefault(i => i.Label == "Scale");
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
                Name = "Scales",
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
                label: "Scales",
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
                Log.Information("Hikvision Security 相机模块 connection status updated: {Status}", hikvisionStatus.Status);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "错误更新Hikvision Security Camera状态在UI线程。");
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
                Log.Information("Hikvision Volume  connection status updated: {Status} for device ID: {DeviceId}", volumeCameraStatus.Status, deviceId ?? "N/A");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error updating Hikvision Volume  status on UI thread.");
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
                Log.Information("Hikvision Industrial  connection status updated: {Status} for device ID: {DeviceId}", industrialCameraStatus.Status, deviceId ?? "N/A");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error updating Hikvision Industrial  status on UI thread.");
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
                Log.Information(" Scale connection status updated: {Status}", weightScaleStatus.Status);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error updating  Scale status on UI thread.");
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
                // 按需加载重量设置
                var weightSettings = _settingsService.LoadSettings<WeightSettings>();
                var currentWeightTimeoutMs = weightSettings.TimeRangeUpper;

                if (_lastReceivedValidWeight.HasValue)
                {
                    package.SetWeight(_lastReceivedValidWeight.Value.WeightInGrams);
                    Log.Information("包裹 {Barcode}: 重量为0，已立即从缓存的流数据补充重量: {称重模块}g", package.Barcode, package.Weight);
                }
                else
                {
                    Log.Information("包裹 {Barcode}: 重量为0，且无可用流缓存，将轮询等待最多 {Timeout}ms...", package.Barcode, currentWeightTimeoutMs);
                    bool weightSupplemented = false;
                    for (int i = 0; i < (currentWeightTimeoutMs / SupplementDataPollIntervalMs); i++)
                    {
                        await Task.Delay(SupplementDataPollIntervalMs);
                        if (!_lastReceivedValidWeight.HasValue) continue;
                        package.SetWeight(_lastReceivedValidWeight.Value.WeightInGrams);
                        Log.Information("包裹 {Barcode}: 轮询等待期间，从流数据补充重量: {称重模块}g (尝试次数: {Attempt})", package.Barcode, package.Weight, i + 1);
                        weightSupplemented = true;
                        break;
                    }
                    if (!weightSupplemented)
                    {
                        Log.Warning("包裹 {Barcode}: 轮询等待 {Timeout}ms 后，仍未从流中获取到有效重量。", package.Barcode, currentWeightTimeoutMs);
                    }
                }
            }

            if (package.Length <= 0 || package.Width <= 0 || package.Height <= 0)
            {
                // 按需加载相机设置
                var cameraSettings = _settingsService.LoadSettings<CameraSettings>();
                var currentVolumeTimeoutMs = cameraSettings.VolumeCameraFusionTimeMs;

                if (_lastReceivedValidVolume.HasValue)
                {
                    var (l, w, h, _) = _lastReceivedValidVolume.Value;
                    package.SetDimensions(l, w, h);
                    Log.Information("包裹 {Barcode}: 尺寸无效，已立即从缓存的流数据补充尺寸: L{L} W{W} H{H}", package.Barcode, package.Length, package.Width, package.Height);
                }
                else
                {
                    Log.Information("包裹 {Barcode}: 尺寸无效，且无可用流缓存，将轮询等待最多 {Timeout}ms...", package.Barcode, currentVolumeTimeoutMs);
                    bool volumeSupplemented = false;
                    for (int i = 0; i < (currentVolumeTimeoutMs / SupplementDataPollIntervalMs); i++)
                    {
                        await Task.Delay(SupplementDataPollIntervalMs);
                        if (_lastReceivedValidVolume.HasValue)
                        {
                            var (l, w, h, _) = _lastReceivedValidVolume.Value;
                            package.SetDimensions(l, w, h);
                            Log.Information("包裹 {Barcode}: 轮询等待期间，从流数据补充尺寸: L{L} W{W} H{H} (尝试次数: {Attempt})", package.Barcode, package.Length, package.Width, package.Height, i + 1);
                            volumeSupplemented = true;
                            break;
                        }
                    }
                    if (!volumeSupplemented)
                    {
                        Log.Warning("包裹 {Barcode}: 轮询等待 {Timeout}ms 后，仍未从流中获取到有效尺寸。", package.Barcode, currentVolumeTimeoutMs);
                    }
                }
            }

            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                CurrentBarcode = package.Barcode;
                UpdatePackageInfoItems(package);
                Interlocked.Increment(ref _totalPackageCount);
            });

            // 检查重量和尺寸是否有效，无效则直接失败
            bool hasWeight = package.Weight > 0;
            bool hasDimensions = package is { Length: > 0, Width: > 0, Height: > 0 };

            if (!hasWeight || !hasDimensions)
            {
                string skipReason = hasWeight switch
                {
                    false when !hasDimensions => "包裹重量和体积信息均缺失",
                    false => "包裹重量信息缺失",
                    _ => "包裹体积信息缺失"
                };

                Log.Warning("包裹 {Barcode}: {Reason}，跳过DCS及后续流程。", package.Barcode, skipReason);
                var combinedErrorMessage = string.IsNullOrEmpty(package.ErrorMessage)
                    ? skipReason
                    : $"{package.ErrorMessage}; {skipReason}";
                package.SetStatus(PackageStatus.Failed, combinedErrorMessage);
                // 后续会在通用代码块中更新UI和历史记录
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
                    package.SetStatus(PackageStatus.Failed, "包裹信息上传DCS失败");
                    Log.Error("包裹 {Barcode}: 包裹基础信息上传DCS失败。", package.Barcode);
                }
                else
                {
                    Log.Information("包裹 {Barcode}: 包裹基础信息上传DCS成功。", package.Barcode);
                    // 3. 调用 sorter.dest_request (仅当 parcel_info_upload 成功)
                    DestRequestResultParams? destResult = await _rookieApiService.RequestDestinationAsync(package.Barcode);
                    if (destResult?.ErrorCode == 0 && int.TryParse(destResult.ChuteCode, out int apiChute) && apiChute > 0)
                    {
                        package.SetChute(apiChute);
                        package.SetStatus(PackageStatus.Success); // 初始成功状态，可能被小车分拣覆盖
                        Log.Information("包裹 {Barcode}: DCS请求目的地成功，分配格口: {Chute}", package.Barcode, apiChute);

                        // 4. 调用小车分拣 (仅当成功获取目的地)
                        Log.Information("包裹 {Barcode}: 准备调用小车分拣服务，目标格口: {Chute}", package.Barcode, package.ChuteNumber);
                        var carSortResult = await _carSortService.ProcessPackageSortingAsync(package);
                        if (carSortResult)
                        {
                            Log.Information("包裹 {Barcode}: 小车分拣服务处理成功。", package.Barcode);
                            // 如果之前是Success, 保持Success。如果ProcessPackageSortingAsync内部修改了状态(例如错误)，以那个为准
                        }
                        else
                        {
                            Log.Warning("包裹 {Barcode}: 小车分拣服务处理失败。", package.Barcode);
                            const string carSortError = "小车分拣失败";
                            var currentErrorMessage = string.IsNullOrEmpty(package.ErrorMessage) ? carSortError : $"{package.ErrorMessage}; {carSortError}";
                            package.SetStatus(PackageStatus.Error, currentErrorMessage); // 或Failed
                        }
                    }
                    else
                    {
                        string destError = destResult == null
                            ? "DCS请求目的地API调用失败"
                            : $"DCS请求目的地逻辑错误: Code {destResult.ErrorCode}, Chute '{destResult.ChuteCode}'";
                        package.SetStatus(PackageStatus.Failed, destError);
                        Log.Error("包裹 {Barcode}: {Error}", package.Barcode, destError);
                    }
                }
            }

            // 5. 调用 sorter.sort_report 上报数据 (无论之前成功与否，只要不是初始重量/体积检查失败就尝试上报)
            // 如果初始重量/体积检查失败，package.Status 已经是 Failed，这里也会正确上报
            string chuteToReport = package.ChuteNumber > 0 ? package.ChuteNumber.ToString() : "N/A"; // API可能需要特定错误格口
            bool successForApiReport = package.Status == PackageStatus.Success;
            string? errorReasonForApiReport = successForApiReport
                ? null
                : (string.IsNullOrEmpty(package.ErrorMessage) ? "未知分拣错误" : package.ErrorMessage);

            Log.Information("包裹 {Barcode}: 准备上报分拣结果到DCS。格口: {Chute}, 状态: {IsSuccess}, 原因: {Reason}",
                package.Barcode, chuteToReport, successForApiReport, errorReasonForApiReport);

            bool reportAcknowledged = await _rookieApiService.ReportSortResultAsync(package.Barcode, chuteToReport, successForApiReport, errorReasonForApiReport);
            if (!reportAcknowledged)
            {
                Log.Warning("包裹 {Barcode}: 上报分拣结果到DCS失败。最终状态: {Status}, 格口: {Chute}",
                    package.Barcode, package.StatusDisplay, chuteToReport);
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
                    Interlocked.Increment(ref _failedPackageCount); // 包括 Error 和 Failed
                UpdateStatistics();
                PackageHistory.Insert(0, package);
                if (PackageHistory.Count > 1000)
                    PackageHistory.RemoveAt(PackageHistory.Count - 1);
                Log.Information("包裹 {Barcode} 处理流程核心逻辑结束, 最终状态: {Status}, 最终错误: {ErrorMessage}", package.Barcode, package.Status, package.ErrorMessage);
            });
            package.ReleaseImage(); // 释放工业相机图像（如果存在）
        }
        catch (Exception ex) // 添加一个通用的try-catch来记录未预料的异常
        {
            Log.Error(ex, "处理包裹 {Barcode} 时发生意外错误。", package.Barcode);
            if (package.Status != PackageStatus.Failed && package.Status != PackageStatus.Error)
            {
                package.SetStatus(PackageStatus.Error, $"处理时发生意外错误: {ex.Message}");
            }
        }
        finally
        {
            _isProcessingPackage = false;
            Log.Information("包裹 {Barcode} 处理流程结束 (或者被忽略后标志位重置)。最终状态: {Status}", package.Barcode, package.StatusDisplay);
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