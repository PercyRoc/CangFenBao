using System.Collections.ObjectModel;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Common.Data;
using Common.Models.Package;
using Common.Models.Settings.ChuteRules;
using Common.Models.Settings.Sort.PendulumSort;
using Common.Services.Settings;
using Common.Services.Ui;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Services;
using Prism.Commands;
using Prism.Dialogs;
using Prism.Mvvm;
using Serilog;
using Serilog.Context;
using SharedUI.Models;
using SortingServices.Pendulum;
using ZtCloudWarehous.Models;
using ZtCloudWarehous.Services;
using ZtCloudWarehous.ViewModels.Settings;
using static Common.Models.Package.PackageStatus;

namespace ZtCloudWarehous.ViewModels;

internal class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly ICameraService _cameraService;
    private readonly IDialogService _dialogService;
    private readonly INotificationService _notificationService;
    private readonly IPackageDataService _packageDataService;
    private readonly ISettingsService _settingsService;
    private readonly IPendulumSortService _sortService;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly DispatcherTimer _timer;
    private readonly IWaybillUploadService _waybillUploadService;
    private readonly IWeighingService _weighingService;
    private string _currentBarcode = string.Empty;
    private BitmapSource? _currentImage;
    private bool _disposed;

    private long _failedPackageCount;
    private long _noReadCount;

    private long _otherErrorCount;

    // 添加峰值效率记录
    private long _peakRate;
    private long _successPackageCount;
    private SystemStatus _systemStatus = new();

    // 添加详细异常计数
    private long _timeoutCount;

    // Add persistent counters
    private long _totalPackageCount;
    private long _weightErrorCount;

    public MainWindowViewModel(IDialogService dialogService,
        ICameraService cameraService,
        PackageTransferService packageTransferService,
        ISettingsService settingsService,
        IPendulumSortService sortService,
        IWeighingService weighingService,
        INotificationService notificationService,
        IPackageDataService packageDataService,
        IWaybillUploadService waybillUploadService)
    {
        _dialogService = dialogService;
        _cameraService = cameraService;
        _settingsService = settingsService;
        _sortService = sortService;
        _weighingService = weighingService;
        _notificationService = notificationService;
        _packageDataService = packageDataService;
        _waybillUploadService = waybillUploadService;
        OpenSettingsCommand = new DelegateCommand(ExecuteOpenSettings);
        OpenHistoryCommand = new DelegateCommand(ExecuteOpenHistory);

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

        // 订阅图像流
        _subscriptions.Add(_cameraService.ImageStream
            .ObserveOn(TaskPoolScheduler.Default) // 使用任务池调度器
            .Subscribe(imageData =>
            {
                try
                {
                    var bitmapSource = imageData;

                    Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
                    {
                        try
                        {
                            // 更新UI
                            CurrentImage = bitmapSource;
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
        _sortService.DeviceConnectionStatusChanged += OnDeviceConnectionStatusChanged;
        _sortService.SortingCompleted += OnSortingCompleted;

        // 订阅包裹流
        // 订阅包裹流 (使用Rx.NET的标准异步处理方式，并行处理)
        _subscriptions.Add(packageTransferService.PackageStream
            .Select(package => Observable.FromAsync(() => OnPackageInfo(package))) // 将每个包裹映射到一个异步操作流
            .Merge(10) // 最多并行处理10个包裹
            .Subscribe(
                _ =>
                {
                    /* 每个包裹处理完成后的OnNext回调，这里无需操作 */
                },
                ex => Log.Error(ex, "包裹处理流发生未处理的致命异常，流已终止。") // 异常处理
            ));
    }

    public DelegateCommand OpenSettingsCommand { get; }
    public DelegateCommand OpenHistoryCommand { get; }

    public string CurrentBarcode
    {
        get => _currentBarcode;
        private set => SetProperty(ref _currentBarcode, value);
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

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void ExecuteOpenSettings()
    {
        _dialogService.ShowDialog("SettingsDialog");
    }

    private void ExecuteOpenHistory()
    {
        _dialogService.ShowDialog("HistoryDialog", null, (Action<IDialogResult>?)null);
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        SystemStatus = SystemStatus.GetCurrentStatus();
    }

    private void InitializeDeviceStatuses()
    {
        try
        {
            Log.Debug("开始初始化设备状态列表");

            // 添加相机状态
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "相机",
                Status = "未连接",
                Icon = "Camera24",
                StatusColor = "#F44336" // 红色表示未连接
            });

            // 加载分拣配置
            var configuration = _settingsService.LoadSettings<PendulumSortConfig>();

            // 添加触发光电状态
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "触发光电",
                Status = "未连接",
                Icon = "Lightbulb24",
                StatusColor = "#F44336" // 红色表示未连接
            });

            // 添加分拣光电状态
            foreach (var photoelectric in configuration.SortingPhotoelectrics)
                DeviceStatuses.Add(new DeviceStatus
                {
                    Name = photoelectric.Name,
                    Status = "未连接",
                    Icon = "Lightbulb24",
                    StatusColor = "#F44336" // 红色表示未连接
                });

            Log.Debug("设备状态列表初始化完成，共 {Count} 个设备", DeviceStatuses.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化设备状态列表时发生错误");
        }
    }

    private void InitializeStatisticsItems()
    {
        StatisticsItems.Add(new StatisticsItem(
            "总包裹数",
            "0",
            "个",
            "累计处理包裹总数",
            "BoxMultiple24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            "成功数",
            "0",
            "个",
            "处理成功的包裹数量",
            "CheckmarkCircle24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            "超时响应",
            "0",
            "个",
            "数据上传超时的包裹数量",
            "Timer24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            "未读包裹",
            "0",
            "个",
            "条码无法识别的包裹数量",
            "ErrorCircle24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            "重量异常",
            "0",
            "个",
            "重量不匹配的包裹数量",
            "Scales24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            "其他异常",
            "0",
            "个",
            "其他异常包裹数量",
            "Alert24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            "处理速率",
            "0",
            "个/小时",
            "每小时处理包裹数量",
            "ArrowTrendingLines24"
        ));

        // 添加峰值效率统计
        StatisticsItems.Add(new StatisticsItem(
            "峰值效率",
            "0",
            "个/小时",
            "最高处理速率",
            "Trophy24"
        ));
    }

    private void InitializePackageInfoItems()
    {
        PackageInfoItems.Add(new PackageInfoItem(
            "重量",
            "0.00",
            "kg",
            "包裹重量",
            "Scales24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "格口",
            "--",
            description: "目标分拣位置",
            icon: "ArrowCircleDown24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "时间",
            "--:--:--",
            description: "处理时间",
            icon: "Timer24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "状态",
            "等待",
            description: "处理状态",
            icon: "Alert24"
        ));
    }

    private void OnCameraConnectionChanged(string? deviceId, bool isConnected)
    {
        try
        {
            var cameraStatus = DeviceStatuses.FirstOrDefault(static x => x.Name == "相机");
            if (cameraStatus == null) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                cameraStatus.Status = isConnected ? "已连接" : "已断开";
                cameraStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新相机状态时发生错误");
        }
    }

    private async Task OnPackageInfo(PackageInfo package)
    {
        // --- 开始应用日志上下文 ---
        var packageContext = $"[包裹{package.Index}|{package.Barcode}]";
        using (LogContext.PushProperty("PackageContext", packageContext))
        {
            Log.Debug("开始处理包裹信息流.");
            try
            {
                // 加载所需设置
                var chuteSettings = _settingsService.LoadSettings<ChuteSettings>();
                var weighingSettings = _settingsService.LoadSettings<WeighingSettings>();

                // 步骤 1: 检查并处理重量
                if (package.Weight <= 0 && weighingSettings.DefaultWeight > 0)
                {
                    package.SetWeight((double)weighingSettings.DefaultWeight);
                    Log.Information("使用预设重量: {DefaultWeight}kg", weighingSettings.DefaultWeight);
                }

                // 步骤 2：处理 NoRead 包裹
                if (string.IsNullOrEmpty(package.Barcode) ||
                    string.Equals(package.Barcode, "noread", StringComparison.OrdinalIgnoreCase))
                {
                    var targetChute = chuteSettings.NoReadChuteNumber > 0
                        ? chuteSettings.NoReadChuteNumber
                        : chuteSettings.ErrorChuteNumber;
                    package.SetChute(targetChute);
                    package.SetStatus(NoRead, "未识别条码");
                    Interlocked.Increment(ref _noReadCount);
                    Log.Warning("条码为空或noread，分配到 NoRead/异常口: {TargetChute}", targetChute);
                }
                else // 有有效条码，执行称重和匹配
                {
                    await HandleValidBarcode(package, chuteSettings);
                }

                // 步骤 关键: 将包裹提交给分拣服务，这是阻塞部分的最后一步
                Log.Debug("将包裹送往分拣服务处理.");
                _sortService.ProcessPackage(package);
                Interlocked.Increment(ref _totalPackageCount);

                // 步骤 后续: 所有非阻塞的后续操作（UI更新、数据库、上传等）都在后台执行
                _ = Task.Run(() => PostProcessingTasks(package));

                Log.Information("包裹核心处理流程完成 (分拣指令已发送).");
            }
            catch (Exception ex) // OnPackageInfo 主流程中的未捕获异常
            {
                HandleTopLevelException(package, ex);
            }
        } // --- 日志上下文结束 ---
    }

    private async Task HandleValidBarcode(PackageInfo package, ChuteSettings chuteSettings)
    {
        Log.Debug("开始上传称重数据.");
        try
        {
            var uploadTask = _weighingService.SendWeightDataAutoAsync(
                package.Barcode,
                Convert.ToDecimal(package.Weight),
                package.Volume.HasValue ? Convert.ToDecimal(package.Volume.Value) : null);
            var timeoutTask = Task.Delay(500);

            var completedTask = await Task.WhenAny(uploadTask, timeoutTask);

            if (completedTask == timeoutTask)
            {
                // 超时处理
                var targetChute = chuteSettings.TimeoutChuteNumber > 0
                    ? chuteSettings.TimeoutChuteNumber
                    : chuteSettings.ErrorChuteNumber;
                Log.Warning("上传称重数据超时 (>{Timeout}ms).", 500);
                package.SetChute(targetChute);
                package.SetStatus(PackageStatus.Timeout, "上传超时");
                Interlocked.Increment(ref _timeoutCount);
            }
            else
            {
                // 上传完成 (成功或失败)
                var weighingResult = await uploadTask;
                Log.Debug("收到称重数据上传响应: Success={IsSuccess}, Code={ErrorCode}, Message={ErrorMessage}",
                    weighingResult.IsSuccess, weighingResult.ErrorCode, weighingResult.ErrorMessage);

                if (weighingResult.IsSuccess)
                {
                    var matchedChute = chuteSettings.FindMatchingChute(package.Barcode, package.Weight);
                    if (matchedChute.HasValue)
                    {
                        package.SetChute(matchedChute.Value);
                        package.SetStatus(Success); // 标记成功
                        Log.Information("匹配到规则，分配到格口: {MatchedChute}", matchedChute.Value);
                    }
                    else
                    {
                        package.SetChute(chuteSettings.ErrorChuteNumber);
                        package.SetStatus(Error, "未匹配规则");
                        Interlocked.Increment(ref _otherErrorCount);
                        Log.Warning("未匹配到规则，分配到异常口: {ErrorChute}", chuteSettings.ErrorChuteNumber);
                    }
                }
                else // API 返回失败
                {
                    HandleWeighingFailure(package, weighingResult, chuteSettings);
                }
            }
        }
        catch (Exception ex) // 上传过程中发生异常
        {
            Log.Error(ex, "上传称重数据时发生异常.");
            package.SetChute(chuteSettings.ErrorChuteNumber);
            package.SetStatus(Error, $"上传异常: {ex.Message}");
            Interlocked.Increment(ref _otherErrorCount);
        }
    }

    private void HandleWeighingFailure(PackageInfo package, WeighingResult weighingResult, ChuteSettings chuteSettings)
    {
        Log.Warning("称重API返回失败. Code: '{ErrorCode}', Message: '{ErrorMessage}'", weighingResult.ErrorCode,
            weighingResult.ErrorMessage);

        // 修正后的逻辑：检查是否为特定的“已出库，不可重复称重”错误
        var isAlreadyShipped = string.Equals(weighingResult.ErrorCode?.Trim(), "OFC_INSIDE_WEIGHT_0021",
                                   StringComparison.OrdinalIgnoreCase)
                               || (weighingResult.ErrorMessage?.Contains("已出库", StringComparison.OrdinalIgnoreCase) ??
                                   false)
                               || (weighingResult.ErrorMessage?.Contains("不可重复称重",
                                   StringComparison.OrdinalIgnoreCase) ?? false);

        if (isAlreadyShipped)
        {
            var infoMessage = string.IsNullOrEmpty(weighingResult.ErrorMessage)
                ? "包裹已出库，不可重复称重"
                : weighingResult.ErrorMessage!;
            _notificationService.ShowWarning($"称重提示: {infoMessage}");

            // 按正常包裹流程进行本地规则匹配
            var matchedChute = chuteSettings.FindMatchingChute(package.Barcode, package.Weight);
            if (matchedChute.HasValue)
            {
                package.SetChute(matchedChute.Value);
                package.SetStatus(Success); // 视为正常包裹
                Log.Information("已出库包裹按正常规则匹配成功，分配到格口: {Chute}", matchedChute.Value);
                return;
            }

            // 未匹配到则按原有规则进入异常口
            package.SetChute(chuteSettings.ErrorChuteNumber);
            package.SetStatus(Error, "未匹配规则");
            Interlocked.Increment(ref _otherErrorCount);
            Log.Warning("已出库包裹未匹配到规则，分配到异常口: {ErrorChute}", chuteSettings.ErrorChuteNumber);
            return;
        }

        // 其他类型的称重失败
        {
            var errorMessage = weighingResult.ErrorMessage ?? "上传失败";
            _notificationService.ShowWarning($"称重失败: {errorMessage}");
            package.SetStatus(Error, errorMessage);
            Interlocked.Increment(ref _otherErrorCount); // 其他异常
            package.SetChute(chuteSettings.ErrorChuteNumber);
        }
    }

    private async Task PostProcessingTasks(PackageInfo package)
    {
        try
        {
            // --- 为后台任务应用独立的日志上下文 ---
            var packageContext = $"[后台任务|{package.Index}|{package.Barcode}]";
            using (LogContext.PushProperty("PackageContext", packageContext))
            {
                // 更新统计和最终状态
                if (package.Status == Success)
                {
                    Interlocked.Increment(ref _successPackageCount);
                    package.SetStatus(Success, "分拣成功");
                    Log.Information("处理成功完成, 最终格口: {Chute}", package.ChuteNumber);
                }
                else
                {
                    Interlocked.Increment(ref _failedPackageCount);
                    Log.Warning("处理失败结束, 状态: {Status}, 格口: {Chute}, 原因: {ErrorMessage}",
                        package.StatusDisplay, package.ChuteNumber, package.ErrorMessage ?? "无错误详情");
                }

                // 更新UI (包裹详情、历史和统计)
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    CurrentBarcode = package.Barcode;
                    UpdatePackageInfoItems(package);
                    PackageHistory.Insert(0, package);
                    if (PackageHistory.Count > 1000) PackageHistory.RemoveAt(PackageHistory.Count - 1);
                    UpdateStatistics();
                });

                // 异步保存到数据库
                await _packageDataService.SavePackageAsync(package);
                Log.Debug("包裹信息已成功异步保存到数据库.");

                // 异步上传到西逸谷服务
                await _waybillUploadService.UploadPackageAndWaitAsync(package);
                Log.Information("西逸谷上传并等待完成.");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "包裹后续处理任务中发生错误");
        }
        finally
        {
            // 确保图像资源总是被释放
            package.ReleaseImage();
            Log.Debug("图像资源已释放.");
        }
    }

    private void HandleTopLevelException(PackageInfo package, Exception ex)
    {
        Log.Error(ex, "处理包裹信息时发生未预料的顶层错误.");
        try
        {
            var chuteSettings = _settingsService.LoadSettings<ChuteSettings>();
            package.SetChute(chuteSettings.ErrorChuteNumber);
            package.SetStatus(Error, $"处理异常: {ex.Message}");
            _sortService.ProcessPackage(package);
            Interlocked.Increment(ref _failedPackageCount);
        }
        catch (Exception innerEx)
        {
            Log.Error(innerEx, "在处理顶层异常时再次发生错误.");
        }
        finally
        {
            package.ReleaseImage();
        }
    }

    private void UpdatePackageInfoItems(PackageInfo package)
    {
        var weightItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "重量");
        if (weightItem != null)
        {
            weightItem.Value = package.Weight.ToString("F2");
            weightItem.Unit = "kg";
        }

        var chuteItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "格口");
        if (chuteItem != null)
        {
            chuteItem.Value = package.ChuteNumber.ToString();
            chuteItem.Description = $"目标分拣位置: {package.ChuteNumber}";
        }

        var timeItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "时间");
        if (timeItem != null)
        {
            timeItem.Value = package.CreateTime.ToString("HH:mm:ss");
            timeItem.Description = $"处理于 {package.CreateTime:yyyy-MM-dd}";
        }

        var statusItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "状态");
        if (statusItem == null) return;

        statusItem.Value = package.StatusDisplay;
        statusItem.Description = package.ErrorMessage ?? package.StatusDisplay;

        statusItem.StatusColor = package.Status switch
        {
            Success
                => "#4CAF50", // Green for success
            Error or PackageStatus.Timeout
                => "#F44336", // Red for error/timeout/failure
            _ => "#2196F3" // Blue for other states (e.g., processing, waiting)
        };
    }

    private void UpdateStatistics()
    {
        var totalItem = StatisticsItems.FirstOrDefault(static x => x.Label == "总包裹数");
        if (totalItem != null)
        {
            // Use the persistent counter
            totalItem.Value = _totalPackageCount.ToString();
            totalItem.Description = $"累计处理 {_totalPackageCount} 个包裹";
        }

        var successItem = StatisticsItems.FirstOrDefault(static x => x.Label == "成功数");
        if (successItem != null)
        {
            // Use the persistent counter
            successItem.Value = _successPackageCount.ToString();
            successItem.Description = $"成功处理 {_successPackageCount} 个包裹";
        }

        // 更新详细异常统计
        var timeoutItem = StatisticsItems.FirstOrDefault(static x => x.Label == "超时响应");
        if (timeoutItem != null)
        {
            timeoutItem.Value = _timeoutCount.ToString();
            timeoutItem.Description = $"共 {_timeoutCount} 个包裹上传超时";
        }

        var noReadItem = StatisticsItems.FirstOrDefault(static x => x.Label == "未读包裹");
        if (noReadItem != null)
        {
            noReadItem.Value = _noReadCount.ToString();
            noReadItem.Description = $"共 {_noReadCount} 个包裹条码无法识别";
        }

        var weightErrorItem = StatisticsItems.FirstOrDefault(static x => x.Label == "重量异常");
        if (weightErrorItem != null)
        {
            weightErrorItem.Value = _weightErrorCount.ToString();
            weightErrorItem.Description = $"共 {_weightErrorCount} 个包裹重量异常";
        }

        var otherErrorItem = StatisticsItems.FirstOrDefault(static x => x.Label == "其他异常");
        if (otherErrorItem != null)
        {
            otherErrorItem.Value = _otherErrorCount.ToString();
            otherErrorItem.Description = $"共 {_otherErrorCount} 个其他异常";
        }

        var rateItem = StatisticsItems.FirstOrDefault(static x => x.Label == "处理速率");
        if (rateItem == null) return;

        {
            var minuteAgo = DateTime.Now.AddMinutes(-1);
            var lastMinuteCount = PackageHistory.Count(p => p.CreateTime > minuteAgo);
            var hourlyRate = lastMinuteCount * 60; // 将每分钟的处理量转换为每小时，并转为整数
            rateItem.Value = hourlyRate.ToString();
            rateItem.Description = $"预计每小时处理 {hourlyRate} 个";

            // 更新峰值效率
            if (hourlyRate > _peakRate)
            {
                _peakRate = hourlyRate;
                var peakRateItem = StatisticsItems.FirstOrDefault(static x => x.Label == "峰值效率");
                if (peakRateItem != null)
                {
                    peakRateItem.Value = _peakRate.ToString();
                    peakRateItem.Description = $"历史最高速率: {_peakRate} 个/小时";
                }
            }
        }
    }

    private void OnDeviceConnectionStatusChanged(object? sender, (string Name, bool Connected) e)
    {
        Log.Debug("设备连接状态变更: {Name} -> {Status}", e.Name, e.Connected ? "已连接" : "已断开");

        Application.Current.Dispatcher.Invoke(() =>
        {
            var deviceStatus = DeviceStatuses.FirstOrDefault(x => x.Name == e.Name);
            if (deviceStatus == null)
            {
                Log.Warning("未找到设备状态项: {Name}", e.Name);
                return;
            }

            deviceStatus.Status = e.Connected ? "已连接" : "已断开";
            deviceStatus.StatusColor = e.Connected ? "#4CAF50" : "#F44336"; // 绿色表示正常，红色表示断开
            Log.Debug("设备状态已更新: {Name} -> {Status}", e.Name, deviceStatus.Status);
        });
    }

    private void OnSortingCompleted(object? sender, PackageInfo completedPackage)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var packageInHistory = PackageHistory.FirstOrDefault(p => p.Index == completedPackage.Index);
            if (packageInHistory == null) return;

            // 使用公共的 SetSortState 方法来更新状态，该方法会同时处理内部状态和显示文本
            packageInHistory.SetSortState(completedPackage.SortState, completedPackage.ErrorMessage);

            Log.Information("[UI更新] 包裹 {Index}|{Barcode} 的最终分拣状态为: {Status}",
                completedPackage.Index, completedPackage.Barcode, completedPackage.StatusDisplay);

            // 如果当前显示的正是这个包裹，也更新主界面的状态显示
            if (CurrentBarcode == packageInHistory.Barcode) UpdatePackageInfoItems(packageInHistory);
        });
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
            try
            {
                // 停止定时器（UI线程操作）
                if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
                    Application.Current.Dispatcher.Invoke(_timer.Stop);
                else
                    _timer.Stop();

                // 停止分拣服务
                if (_sortService.IsRunning())
                    try
                    {
                        // 使用超时避免无限等待
                        var stopTask = _sortService.StopAsync();
                        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5));
                        var completedTask = Task.WhenAny(stopTask, timeoutTask).Result;

                        if (completedTask == stopTask)
                            Log.Information("摆轮分拣服务已停止");
                        else
                            Log.Warning("摆轮分拣服务停止超时");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "停止摆轮分拣服务时发生错误");
                    }

                // 释放分拣服务资源
                if (_sortService is IDisposable disposableSortService)
                {
                    disposableSortService.Dispose();
                    Log.Information("摆轮分拣服务资源已释放");
                }

                // 取消订阅事件
                _sortService.DeviceConnectionStatusChanged -= OnDeviceConnectionStatusChanged;
                _sortService.SortingCompleted -= OnSortingCompleted;
                _cameraService.ConnectionChanged -= OnCameraConnectionChanged;

                // 释放订阅
                foreach (var subscription in _subscriptions)
                    try
                    {
                        subscription.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "释放订阅时发生错误");
                    }

                _subscriptions.Clear();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "释放资源时发生错误");
            }

        _disposed = true;
    }
}