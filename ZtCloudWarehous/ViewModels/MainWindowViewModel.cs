using System.Collections.ObjectModel;
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
using Prism.Mvvm;
using Serilog;
using SharedUI.Models;
using SortingServices.Pendulum;
using ZtCloudWarehous.Models;
using ZtCloudWarehous.Services;
using ZtCloudWarehous.ViewModels.Settings;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Prism.Services.Dialogs;
using static Common.Models.Package.PackageStatus;
using Serilog.Context;

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
    private readonly IWeighingService _weighingService;
    private readonly IWaybillUploadService _waybillUploadService;
    private string _currentBarcode = string.Empty;
    private BitmapSource? _currentImage;
    private bool _disposed;
    private SystemStatus _systemStatus = new();

    // Add persistent counters
    private long _totalPackageCount;
    private long _successPackageCount;
    private long _failedPackageCount;
    // 添加详细异常计数
    private long _timeoutCount;
    private long _noReadCount;
    private long _weightErrorCount;
    private long _otherErrorCount;
    // 添加峰值效率记录
    private long _peakRate;

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
        // 订阅包裹流
        _subscriptions.Add(packageTransferService.PackageStream
            .Subscribe(package => { Application.Current.Dispatcher.BeginInvoke(() => OnPackageInfo(package)); }));
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
        _dialogService.ShowDialog("HistoryDialog", null, null, "HistoryWindow");
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
            Label = "超时响应",
            Value = "0",
            Unit = "个",
            Description = "数据上传超时的包裹数量",
            Icon = "Timer24",
        });

        StatisticsItems.Add(new StatisticsItem
        {
            Label = "未读包裹",
            Value = "0",
            Unit = "个",
            Description = "条码无法识别的包裹数量",
            Icon = "ErrorCircle24",
        });

        StatisticsItems.Add(new StatisticsItem
        {
            Label = "重量异常",
            Value = "0",
            Unit = "个",
            Description = "重量不匹配的包裹数量",
            Icon = "Scales24",
        });

        StatisticsItems.Add(new StatisticsItem
        {
            Label = "其他异常",
            Value = "0",
            Unit = "个",
            Description = "其他异常包裹数量",
            Icon = "Alert24",
        });

        StatisticsItems.Add(new StatisticsItem
        {
            Label = "处理速率",
            Value = "0",
            Unit = "个/小时",
            Description = "每小时处理包裹数量",
            Icon = "ArrowTrendingLines24"
        });

        // 添加峰值效率统计
        StatisticsItems.Add(new StatisticsItem
        {
            Label = "峰值效率",
            Value = "0",
            Unit = "个/小时",
            Description = "最高处理速率",
            Icon = "Trophy24"
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
            Label = "格口",
            Value = "--",
            Description = "目标分拣位置",
            Icon = "ArrowCircleDown24"
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
            Value = "等待",
            Description = "处理状态",
            Icon = "Alert24"
        });
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

    private async void OnPackageInfo(PackageInfo package)
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
                Log.Debug("已加载 ChuteSettings 和 WeighingSettings.");

                // 步骤 1: 检查并处理重量
                if (package.Weight <= 0)
                {
                    Log.Debug("包裹重量为 0 或无效.");
                    if (weighingSettings.DefaultWeight > 0)
                    {
                        package.SetWeight((double)weighingSettings.DefaultWeight);
                        Log.Information("使用预设重量: {DefaultWeight}kg", weighingSettings.DefaultWeight);
                    }
                    else
                    {
                        Log.Warning("未设置预设重量.");
                        // 注意: 这里可以选择性地标记为重量异常或分配到错误口
                    }
                }

                // 步骤 2：更新当前条码 (UI)
                Application.Current.Dispatcher.Invoke(() =>
                {
                    try { CurrentBarcode = package.Barcode; }
                    catch (Exception ex) { Log.Error(ex, "更新当前条码UI时发生错误"); }
                });

                // 步骤 3：处理 NoRead 包裹
                if (string.IsNullOrEmpty(package.Barcode) ||
                    string.Equals(package.Barcode, "noread", StringComparison.OrdinalIgnoreCase))
                {
                    var targetChute = chuteSettings.NoReadChuteNumber > 0 ? chuteSettings.NoReadChuteNumber : chuteSettings.ErrorChuteNumber;
                    package.SetChute(targetChute);
                    package.SetStatus(Error, "未识别条码");
                    Interlocked.Increment(ref _noReadCount);
                    Log.Warning("条码为空或noread，分配到 NoRead/异常口: {TargetChute}", targetChute);
                }
                else // 有有效条码
                {
                    Log.Debug("开始上传称重数据.");
                    // 步骤 4：上传包裹并处理响应
                    try
                    {
                        var request = new WeighingRequest
                        {
                            WaybillCode = package.Barcode,
                            ActualWeight = Convert.ToDecimal(package.Weight),
                            ActualVolume = package.Volume.HasValue ? Convert.ToDecimal(package.Volume.Value) : 0
                        };

                        var uploadTask = _weighingService.SendWeightDataAsync(request);
                        var timeoutTask = Task.Delay(500); // 500ms timeout

                        var completedTask = await Task.WhenAny(uploadTask, timeoutTask);

                        if (completedTask == timeoutTask)
                        {
                            // 超时处理
                            var targetChute = chuteSettings.TimeoutChuteNumber > 0 ? chuteSettings.TimeoutChuteNumber : chuteSettings.ErrorChuteNumber;
                            Log.Warning("上传称重数据超时 (>{Timeout}ms).", 500);
                            _notificationService.ShowWarning($"上传称重数据超时: {package.Barcode}");
                            package.SetChute(targetChute);
                            package.SetStatus(PackageStatus.Timeout, "上传超时");
                            Interlocked.Increment(ref _timeoutCount);
                            Log.Warning("分配到 Timeout/异常口: {TargetChute}", targetChute);
                        }
                        else
                        {
                            // 上传完成 (成功或失败)
                            var response = await uploadTask;
                            Log.Debug("收到称重数据上传响应: Success={Success}, Code={Code}, Message={Message}",
                                response.Success, response.Code, response.Message);

                            bool treatAsSuccess = response.Success || (response.Message != null && response.Message.Contains("出库完成"));

                            if (treatAsSuccess)
                            {
                                if (!response.Success) Log.Information("响应消息为 '出库完成'，视为成功处理.");

                                // 查找匹配规则
                                var matchedChute = chuteSettings.FindMatchingChute(package.Barcode);
                                if (matchedChute.HasValue)
                                {
                                    package.SetChute(matchedChute.Value);
                                    package.SetStatus(Success); // 标记成功
                                    Log.Information("匹配到规则，分配到格口: {MatchedChute}", matchedChute.Value);
                                }
                                else
                                {
                                    // 无规则匹配
                                    package.SetChute(chuteSettings.ErrorChuteNumber);
                                    package.SetStatus(Error, response.Success ? "未匹配规则" : "出库完成但未匹配规则");
                                    Interlocked.Increment(ref _otherErrorCount);
                                    Log.Warning("未匹配到规则，分配到异常口: {ErrorChute}", chuteSettings.ErrorChuteNumber);
                                }
                            }
                            else // API 返回失败且非 "出库完成"
                            {
                                Log.Warning("上传称重数据API返回失败.");
                                _notificationService.ShowWarning($"上传称重数据失败: {response.Message}");
                                int targetChute;
                                if (response.Message != null && response.Message.Contains("重量"))
                                {
                                    targetChute = chuteSettings.WeightMismatchChuteNumber > 0 ? chuteSettings.WeightMismatchChuteNumber : chuteSettings.ErrorChuteNumber;
                                    Interlocked.Increment(ref _weightErrorCount);
                                    Log.Warning("失败原因为重量相关，分配到重量异常/异常口: {TargetChute}", targetChute);
                                }
                                else if (response.Message != null && response.Message.Contains("拦截"))
                                {
                                    targetChute = chuteSettings.ErrorChuteNumber;
                                    Interlocked.Increment(ref _otherErrorCount);
                                    Log.Warning("失败原因为拦截，分配到异常口: {TargetChute}", targetChute);
                                }
                                else
                                {
                                    targetChute = chuteSettings.ErrorChuteNumber;
                                    Interlocked.Increment(ref _otherErrorCount);
                                    Log.Warning("失败原因未知或其他，分配到异常口: {TargetChute}", targetChute);
                                }
                                package.SetChute(targetChute);
                                package.SetStatus(Error, $"上传失败: {response.Message}");
                            }
                        }
                    }
                    catch (Exception ex) // 上传过程中发生异常
                    {
                        Log.Error(ex, "上传称重数据时发生异常.");
                        _notificationService.ShowError($"上传称重数据异常: {ex.Message}");
                        package.SetChute(chuteSettings.ErrorChuteNumber);
                        package.SetStatus(Error, $"上传异常: {ex.Message}");
                        Interlocked.Increment(ref _otherErrorCount);
                        Log.Warning("分配到异常口: {ErrorChute}", chuteSettings.ErrorChuteNumber);
                    }
                } // End of valid barcode processing

                // 步骤 5: 调用分拣服务处理
                Log.Debug("将包裹送往分拣服务处理.");
                _sortService.ProcessPackage(package);
                Interlocked.Increment(ref _totalPackageCount);

                // 步骤 6: 更新统计和最终状态
                if (package.Status != Error && package.Status != PackageStatus.Timeout)
                {
                    Interlocked.Increment(ref _successPackageCount);
                    // 如果之前是 Success，可以不再设置或设置更具体的成功状态如 SortSuccess
                    if (package.Status == Success) // 只有原始成功状态才更新为最终成功
                    {
                        package.SetStatus(Success, "分拣成功"); // 确保最终状态是 Success
                    }
                    Log.Information("处理成功完成, 最终格口: {Chute}", package.ChuteNumber);
                }
                else
                {
                    Interlocked.Increment(ref _failedPackageCount);
                    // 使用之前的 Warning 日志级别，记录失败的最终状态和原因
                    Log.Warning("处理失败结束, 状态: {Status}, 格口: {Chute}, 原因: {ErrorMessage}",
                                package.StatusDisplay, package.ChuteNumber, package.ErrorMessage ?? "无错误详情");
                }

                // 步骤 7: 更新 UI (包裹详情)
                Application.Current.Dispatcher.Invoke(() =>
                {
                    try { UpdatePackageInfoItems(package); }
                    catch (Exception ex) { Log.Error(ex, "更新包裹详情UI时发生错误"); }
                });

                // 步骤 8: 更新 UI (历史和统计)
                Application.Current.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        PackageHistory.Insert(0, package);
                        while (PackageHistory.Count > 1000) PackageHistory.RemoveAt(PackageHistory.Count - 1);
                        UpdateStatistics();
                    }
                    catch (Exception ex) { Log.Error(ex, "更新历史和统计UI时发生错误"); }
                });

                // 步骤 9: 异步保存到数据库
                Log.Debug("准备启动后台任务保存到数据库.");
                var contextForDbTask = packageContext; // 捕获上下文
                var barcodeForDbTask = package.Barcode; // 捕获条码
                _ = Task.Run(async () =>
                {
                    using (LogContext.PushProperty("PackageContext", contextForDbTask)) // 恢复上下文
                    {
                        try
                        {
                            await _packageDataService.AddPackageAsync(package);
                            Log.Debug("后台数据库保存成功."); // 使用 Debug
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "后台数据库保存失败.");
                            // UI Notification should happen on UI thread if needed, consider event aggregation
                            // _notificationService.ShowError($"保存包裹记录失败：{ex.Message}");
                        }
                    }
                });

                // 步骤 10: 上传到西逸谷服务并等待
                try
                {
                    Log.Debug("开始上传到西逸谷服务并等待.");
                    await _waybillUploadService.UploadPackageAndWaitAsync(package);
                    Log.Information("西逸谷上传并等待完成.");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "等待西逸谷上传完成时发生错误.");
                    _notificationService.ShowWarning($"西逸谷上传等待失败：{ex.Message}"); // 保留警告
                }

                Log.Information("包裹处理流程完成.");

            }
            catch (Exception ex) // OnPackageInfo 主流程中的未捕获异常
            {
                Log.Error(ex, "处理包裹信息时发生未预料的顶层错误.");
                try
                {
                    // 尝试记录异常状态并分配到错误口
                    var chuteSettings = _settingsService.LoadSettings<ChuteSettings>();
                    package.SetChute(chuteSettings.ErrorChuteNumber);
                    package.SetStatus(Error, $"处理异常: {ex.Message}");
                    Log.Warning("因顶层处理异常，分配到异常口: {ErrorChute}", chuteSettings.ErrorChuteNumber);
                    // 可能需要调用 _sortService.ProcessPackage(package) 来确保包裹被送走
                    _sortService.ProcessPackage(package);
                    Interlocked.Increment(ref _failedPackageCount); // 计入失败
                }
                catch (Exception innerEx)
                {
                    Log.Error(innerEx, "在处理顶层异常时再次发生错误.");
                }
            }
            finally
            {
                // 确保图像资源总是被释放
                package.ReleaseImage();
                Log.Debug("图像资源已释放.");
            }
        } // --- 日志上下文结束 ---
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

    protected virtual void Dispose(bool disposing)
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