using System.Collections.ObjectModel;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Camera.Interface;
using Common.Models;
using Common.Models.Package;
using Common.Services.Ui;
using History.Configuration;
using History.Data;
using LosAngelesExpress.Services;
using Serilog;
using System.Diagnostics;

namespace LosAngelesExpress.ViewModels;

public class MainViewModel: BindableBase, IDisposable
{
    private readonly IDialogService _dialogService;
    private readonly ICameraService _huaRayCameraService;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly DispatcherTimer _timer;
    private readonly INotificationService _notificationService;
    private readonly IPackageHistoryDataService _historyDataService;
    private readonly ICainiaoApiService _cainiaoApiService;

    private string _currentBarcode = string.Empty;
    private BitmapSource? _currentImage;
    private bool _disposed;
    private SystemStatus _systemStatus = new();

    private long _totalPackageCount;
    private long _successPackageCount;
    private long _failedPackageCount;
    private long _peakRate;

    private volatile bool _isProcessingPackage;

    public MainViewModel(
        IDialogService dialogService,
        INotificationService notificationService,
        ICameraService huaRayCameraService,
        IPackageHistoryDataService historyDataService,
        ICainiaoApiService cainiaoApiService)
    {
        _dialogService = dialogService;
        _notificationService = notificationService;
        _huaRayCameraService = huaRayCameraService;
        _historyDataService = historyDataService;
        _cainiaoApiService = cainiaoApiService;

        // 初始化命令
        OpenSettingsCommand = new DelegateCommand(ExecuteOpenSettings);
        OpenHistoryCommand = new DelegateCommand(ExecuteOpenHistory);

        // 初始化定时器
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        // 初始化各种状态和信息项
        InitializeDeviceStatuses();
        InitializeStatisticsItems();
        InitializePackageInfoItems();

        // 订阅华睿相机连接状态变化
        _huaRayCameraService.ConnectionChanged += OnHuaRayCameraConnectionChanged;

        // 订阅华睿相机图像流到实时图像区域
        _subscriptions.Add(_huaRayCameraService.ImageStreamWithId
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
                            Log.Error(ex, "从华睿相机实时流更新UI线程上的CurrentImage时出错。");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理华睿相机实时流中的图像时出错。");
                }
            }, ex => Log.Error(ex, "华睿相机实时图像流订阅出错。")));

        // 订阅包裹信息流并处理包裹
        _subscriptions.Add(_huaRayCameraService.PackageStream
            .ObserveOn(TaskPoolScheduler.Default) // 在后台线程处理包裹
            .Subscribe(packageInfo =>
            {
                // 确保在后台线程安全地启动异步处理任务
                _ = Task.Run(() => ProcessPackageAsync(packageInfo));
            }, ex => Log.Error(ex, "包裹信息流订阅出错。")));

        // 在构造函数末尾，主动查询并更新相机服务的初始连接状态
        UpdateInitialDeviceStatus(_huaRayCameraService, "HuaRay Camera");

        Log.Information("洛杉矶快手 MainViewModel 初始化完成。");
    }

    ~MainViewModel()
    {
        Dispose(false);
    }

    #region Properties

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

    public SystemStatus SystemStatus
    {
        get => _systemStatus;
        private set => SetProperty(ref _systemStatus, value);
    }

    public ObservableCollection<PackageInfo> PackageHistory { get; } = [];
    public ObservableCollection<StatisticsItem> StatisticsItems { get; } = [];
    public ObservableCollection<DeviceStatus> DeviceStatuses { get; } = [];
    public ObservableCollection<PackageInfoItem> PackageInfoItems { get; } = [];

    #endregion

    #region Command Implementations

    private void ExecuteOpenSettings()
    {
        try
        {
            _dialogService.ShowDialog("SettingsDialog", new DialogParameters(), _ =>
            { });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "打开设置对话框时发生错误。");
            _notificationService.ShowError("Failed to open settings dialog.");
        }
    }

    private void ExecuteOpenHistory()
    {
        try
        {
            Log.Information("准备打开历史记录对话框...");

            // 创建用于配置历史记录视图的参数
            var historyParams = new DialogParameters();
            var historyConfig = new HistoryViewConfiguration
            {
                ColumnSpecs =
                [
                    // 序号
                    new HistoryColumnSpec { PropertyName = "Index", IsDisplayed = true, HeaderResourceKey = "PackageHistory_Header_Index", DisplayOrderInGrid = 0 },
                    // 条码
                    new HistoryColumnSpec { PropertyName = "Barcode", IsDisplayed = true, HeaderResourceKey = "PackageHistory_Header_Barcode", DisplayOrderInGrid = 1 },
                    // 重量
                    new HistoryColumnSpec { PropertyName = "Weight", IsDisplayed = true, HeaderResourceKey = "PackageHistory_Header_Weight", DisplayOrderInGrid = 2, StringFormat = "F2" },
                    // 尺寸 (Length, Width, Height)
                    new HistoryColumnSpec { PropertyName = "Length", IsDisplayed = true, HeaderResourceKey = "PackageHistory_Header_Length", DisplayOrderInGrid = 3, StringFormat = "F1" },
                    new HistoryColumnSpec { PropertyName = "Width", IsDisplayed = true, HeaderResourceKey = "PackageHistory_Header_Width", DisplayOrderInGrid = 4, StringFormat = "F1" },
                    new HistoryColumnSpec { PropertyName = "Height", IsDisplayed = true, HeaderResourceKey = "PackageHistory_Header_Height", DisplayOrderInGrid = 5, StringFormat = "F1" },
                    // 时间 (CreateTime)
                    new HistoryColumnSpec { PropertyName = "CreateTime", IsDisplayed = true, HeaderResourceKey = "PackageHistory_Header_CreateTime", DisplayOrderInGrid = 6, StringFormat = "yyyy-MM-dd HH:mm:ss" },
                    // 状态 (StatusDisplay)
                    new HistoryColumnSpec { PropertyName = "Status", IsDisplayed = true, HeaderResourceKey = "PackageHistory_Header_Status", DisplayOrderInGrid = 7 },
                    // 图像查看按钮 (ImageAction)
                    new HistoryColumnSpec { PropertyName = "ImageAction", IsDisplayed = true, HeaderResourceKey = "PackageHistory_Header_ImageAction", DisplayOrderInGrid = 8, IsTemplateColumn = true, Width = "Auto" }
                ]
            };
            
            // 将配置添加到对话框参数中
            historyParams.Add("customViewConfiguration", historyConfig);
            historyParams.Add("title", "Package History - Los Angeles Express");
            _dialogService.ShowDialog("PackageHistoryDialogView", historyParams, _ => { });

        }
        catch (Exception ex)
        {
            Log.Error(ex, "打开历史记录对话框时发生错误。");
            _notificationService.ShowError("Failed to open history dialog.");
        }
    }

    #endregion

    #region Timer and Status Updates

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

    #endregion

    #region Initialization Methods

    private void InitializeDeviceStatuses()
    {
        try
        {
            DeviceStatuses.Clear();
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "HuaRay Camera",
                Status = "Disconnected",
                Icon = "Camera24",
                StatusColor = "#F44336"
            });
            Log.Information("设备状态列表初始化完成。");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化设备状态列表时发生错误。");
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
                label: "Exception",
                value: "0",
                unit: "pcs",
                description: "Exception packages (errors, timeouts)",
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

            Log.Information("统计信息项目初始化完成。");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化统计信息项目时发生错误。");
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

            PackageInfoItems.Add(new PackageInfoItem(
                label: "Dimensions",
                value: "-- x -- x --",
                unit: "mm",
                description: "L x W x H",
                icon: "ScanObject24"
            ));

            PackageInfoItems.Add(new PackageInfoItem(
                label: "Destination",
                value: "--",
                unit: "Chute",
                description: "Assigned sorting chute",
                icon: "BranchFork24"
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

            Log.Information("包裹信息项目初始化完成。");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化包裹信息项目时发生错误。");
        }
    }

    #endregion

    #region Device Status Event Handlers

    private void OnHuaRayCameraConnectionChanged(string? deviceId, bool isConnected)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var cameraStatus = DeviceStatuses.FirstOrDefault(d => d.Name == "HuaRay Camera");
                if (cameraStatus == null) return;
                
                cameraStatus.Status = isConnected ? "Connected" : "Disconnected";
                cameraStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
                Log.Information("华睿相机连接状态已更新: {Status}，设备ID: {DeviceId}", cameraStatus.Status, deviceId ?? "N/A");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "在UI线程更新华睿相机状态时发生错误。");
            }
        });
    }

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

    #endregion

    #region Package Processing

    private async Task ProcessPackageAsync(PackageInfo package)
    {
        if (_isProcessingPackage)
        {
            Log.Information("包裹 {Barcode}: 当前正在处理另一个包裹，此包裹被忽略。", package.Barcode);
            package.ReleaseImage();
            package.Dispose();
            return;
        }

        _isProcessingPackage = true;
        var processingStopwatch = Stopwatch.StartNew();

        try
        {
            // 1. 处理前先更新总数
            Interlocked.Increment(ref _totalPackageCount);

            Log.Information("开始处理包裹: {Barcode}, 触发时间: {CreateTime:HH:mm:ss.fff}", package.Barcode, package.CreateTime);

            // 2. 收到包裹后，先更新CurrentBarcode和包裹信息项 (UI线程)
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                CurrentBarcode = package.Barcode;
                UpdatePackageInfoItems(package);
            });

            // 3. 检查重量和尺寸
            bool hasWeight = package.Weight > 0;
            bool hasDimensions = package is { Length: > 0, Width: > 0, Height: > 0 };
            bool hasValidData = hasWeight && hasDimensions;
            string status = "Success";
            string? errorMessage = null;

            if (!hasWeight && !hasDimensions)
            {
                status = "Missing weight and dimension data";
                errorMessage = status;
            }
            else if (!hasWeight)
            {
                status = "Missing weight data";
                errorMessage = status;
            }
            else if (!hasDimensions)
            {
                status = "Missing dimension data";
                errorMessage = status;
            }

            // 4. 上传到菜鸟API（仅有数据时才上传）
            if (hasValidData)
            {
                try
                {
                    Log.Information("开始上传包裹 {Barcode} 到菜鸟API...", package.Barcode);
                    var uploadResult = await _cainiaoApiService.UploadPackageAsync(package);

                    if (uploadResult.IsSuccess)
                    {
                        Log.Information("包裹 {Barcode} 菜鸟API上传成功: SortCode={SortCode}, 耗时={ResponseTime}ms",
                            package.Barcode, uploadResult.SortCode ?? "N/A", uploadResult.ResponseTimeMs);
                    
                    }
                    else
                    {
                        status = $"API error: {uploadResult.ErrorMessage}";
                        errorMessage = status;
                        Log.Warning("包裹 {Barcode} 菜鸟API上传失败: {ErrorMessage}, HTTP状态码={HttpStatus}, 耗时={ResponseTime}ms",
                            package.Barcode, uploadResult.ErrorMessage, uploadResult.HttpStatusCode, uploadResult.ResponseTimeMs);
                        _notificationService.ShowWarning($"Cainiao API upload failed for {package.Barcode}: {uploadResult.ErrorMessage}");
                    }
                }
                catch (Exception ex)
                {
                    status = $"API exception: {ex.Message}";
                    errorMessage = status;
                    Log.Error(ex, "包裹 {Barcode}: 上传到菜鸟API时发生未预期错误。", package.Barcode);
                    _notificationService.ShowWarning($"Cainiao API upload error for {package.Barcode}: {ex.Message}");
                }
            }

            // 5. 设置包裹状态
            package.SetStatus(status);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                package.ErrorMessage = errorMessage;
                Interlocked.Increment(ref _failedPackageCount);
                _notificationService.ShowWarning($"Package {package.Barcode} processing failed: {errorMessage}");
            }
            else
            {
                Interlocked.Increment(ref _successPackageCount);
                _notificationService.ShowSuccess($"Package {package.Barcode} processed successfully");
            }

            // 6. 保存历史记录
            try
            {
                await _historyDataService.AddPackageAsync(PackageHistoryRecord.FromPackageInfo(package));
                Log.Information("包裹 {Barcode}: 历史记录保存完成或已发起。", package.Barcode);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "包裹 {Barcode}: 保存历史记录时发生错误。", package.Barcode);
            }

            // 7. 更新UI (最终状态，在UI线程)
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                UpdatePackageInfoItems(package);
                UpdateStatistics();
                PackageHistory.Insert(0, package);
                if (PackageHistory.Count > 1000)
                    PackageHistory.RemoveAt(PackageHistory.Count - 1);
            });

            Log.Information("包裹 {Barcode} 处理完成, 最终状态: {Status}", package.Barcode, package.StatusDisplay);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹 {Barcode} 时发生意外错误。", package.Barcode);
            package.SetStatus($"Unexpected error: {ex.Message}");
            package.ErrorMessage = $"Unexpected error: {ex.Message}";
            Interlocked.Increment(ref _failedPackageCount);
            _notificationService.ShowError($"Package {package.Barcode} processing failed");
        }
        finally
        {
            processingStopwatch.Stop();
            package.ProcessingTime = (int)processingStopwatch.ElapsedMilliseconds;
            _isProcessingPackage = false;
            package.ReleaseImage(); // 确保处理完成后释放图像资源
            Log.Information("包裹 {Barcode} 处理流程结束，总耗时: {TotalTime}ms", package.Barcode, package.ProcessingTime);
        }
    }

    private void UpdatePackageInfoItems(PackageInfo package)
    {
        try
        {
            var weightItem = PackageInfoItems.FirstOrDefault(i => i.Label == "Weight");
            if (weightItem != null) 
            { 
                weightItem.Value = package.Weight.ToString("F2"); 
            }

            var dimensionsItem = PackageInfoItems.FirstOrDefault(i => i.Label == "Dimensions");
            if (dimensionsItem != null)
            {
                dimensionsItem.Value = package is { Length: > 0, Width: > 0, Height: > 0 } 
                    ? $"{package.Length.Value:F0}x{package.Width.Value:F0}x{package.Height.Value:F0}" 
                    : "-- x -- x --";
            }

            var destinationItem = PackageInfoItems.FirstOrDefault(i => i.Label == "Destination");
            if (destinationItem != null)
            {
                destinationItem.Value = package.ChuteNumber > 0 
                    ? package.ChuteNumber.ToString() 
                    : (!string.IsNullOrEmpty(package.ErrorMessage) ? "ERR" : "--");
            }

            var timeItem = PackageInfoItems.FirstOrDefault(i => i.Label == "Time");
            if (timeItem != null) 
            { 
                timeItem.Value = package.CreateTime.ToString("HH:mm:ss"); 
            }

            var statusItem = PackageInfoItems.FirstOrDefault(i => i.Label == "Status");
            if (statusItem != null)
            {
                statusItem.Value = package.StatusDisplay;
                statusItem.Description = package.ErrorMessage ?? package.StatusDisplay;
                statusItem.StatusColor = string.IsNullOrEmpty(package.ErrorMessage) ? "#4CAF50" : "#F44336";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新包裹信息项目时发生错误。");
        }
    }

    private void UpdateStatistics()
    {
        try
        {
            var totalItem = StatisticsItems.FirstOrDefault(i => i.Label == "Total Packages");
            if (totalItem != null) totalItem.Value = _totalPackageCount.ToString();

            var successItem = StatisticsItems.FirstOrDefault(i => i.Label == "Success");
            if (successItem != null) successItem.Value = _successPackageCount.ToString();

            var exceptionItem = StatisticsItems.FirstOrDefault(i => i.Label == "Exception");
            if (exceptionItem != null) exceptionItem.Value = _failedPackageCount.ToString();

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
        catch (Exception ex)
        {
            Log.Error(ex, "更新统计信息时发生错误。");
        }
    }

    #endregion

    #region IDisposable Implementation

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
                Log.Information("正在释放 MainViewModel 资源...");
                
                _timer.Stop();
                _timer.Tick -= Timer_Tick;
                _huaRayCameraService.ConnectionChanged -= OnHuaRayCameraConnectionChanged;

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

                Log.Information("MainViewModel 资源释放完成。");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "MainViewModel 释放期间发生错误。");
            }
        }

        _disposed = true;
    }

    #endregion
}