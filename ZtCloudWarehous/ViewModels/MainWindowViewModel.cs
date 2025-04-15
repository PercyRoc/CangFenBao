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
        try
        {
            // Load settings once
            var chuteSettings = _settingsService.LoadSettings<ChuteSettings>();
            var weighingSettings = _settingsService.LoadSettings<WeighingSettings>();

            if (package.Weight <= 0)
            {
                if (weighingSettings.DefaultWeight > 0)
                {
                    package.SetWeight((double)weighingSettings.DefaultWeight);
                    Log.Information("包裹 {Barcode} 重量为0或无效，使用预设重量：{DefaultWeight}kg", package.Barcode, weighingSettings.DefaultWeight);
                }
                else
                {
                    // If weight is invalid and no default is set, treat as an error?
                    // For now, just log a warning as before. User might want to assign WeightMismatchChute later.
                    Log.Warning("包裹 {Barcode} 重量为0，且未设置预设重量", package.Barcode);
                    // Optionally, assign to WeightMismatchChuteNumber here if required:
                    // package.SetChute(chuteSettings.WeightMismatchChuteNumber);
                    // package.SetStatus(Error, "无效重量");
                    // Log.Warning("包裹 {Barcode} 重量无效，使用重量不匹配口：{WeightMismatchChute}", package.Barcode, chuteSettings.WeightMismatchChuteNumber);
                }
            }

            // 第二步：更新当前条码
            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    CurrentBarcode = package.Barcode;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "更新当前条码时发生错误");
                }
            });

            // 第三步：判断是否为noread包裹
            if (string.IsNullOrEmpty(package.Barcode) ||
                string.Equals(package.Barcode, "noread", StringComparison.OrdinalIgnoreCase))
            {
                package.SetChute(chuteSettings.NoReadChuteNumber > 0 ? chuteSettings.NoReadChuteNumber : chuteSettings.ErrorChuteNumber);
                package.SetStatus(Error, "未识别条码");
                Interlocked.Increment(ref _noReadCount);
                Log.Warning("包裹条码为空或noread，使用NoRead口(或异常口): {NoReadChute}",
                    chuteSettings.NoReadChuteNumber > 0 ? chuteSettings.NoReadChuteNumber : chuteSettings.ErrorChuteNumber);
            }
            else
            {
                // 第四步：上传包裹并获取返回值
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
                        // Upload Timeout
                        Log.Warning("上传称重数据超时: {Barcode}", package.Barcode);
                        _notificationService.ShowWarning($"上传称重数据超时: {package.Barcode}");
                        package.SetChute(chuteSettings.TimeoutChuteNumber > 0 ? chuteSettings.TimeoutChuteNumber : chuteSettings.ErrorChuteNumber);
                        package.SetStatus(PackageStatus.Timeout, "上传超时");
                        Interlocked.Increment(ref _timeoutCount);
                        Log.Warning("包裹 {Barcode} 上传超时，使用超时口(或异常口)：{TimeoutChute}", package.Barcode,
                            chuteSettings.TimeoutChuteNumber > 0 ? chuteSettings.TimeoutChuteNumber : chuteSettings.ErrorChuteNumber);
                    }
                    else
                    {
                        // Upload Completed
                        var response = await uploadTask;
                        if (!response.Success)
                        {
                            // Upload Failed (API Error)
                            Log.Warning("上传称重数据失败: Code={Code}, Message={Message}", response.Code, response.Message);
                            _notificationService.ShowWarning($"上传称重数据失败: {response.Message}");
                            if (response.Message.Contains("重量"))
                            {
                                package.SetChute(chuteSettings.WeightMismatchChuteNumber > 0 ? chuteSettings.WeightMismatchChuteNumber : chuteSettings.ErrorChuteNumber);
                                Interlocked.Increment(ref _weightErrorCount);
                            }
                            else if (response.Message.Contains("拦截"))
                            {
                                package.SetChute(chuteSettings.WeightMismatchChuteNumber > 0 ? chuteSettings.WeightMismatchChuteNumber : chuteSettings.ErrorChuteNumber);
                            }
                            else
                            {
                                package.SetChute(chuteSettings.ErrorChuteNumber);
                                Interlocked.Increment(ref _otherErrorCount);
                            }
                            package.SetStatus(Error, $"上传失败: {response.Message}");
                            Log.Warning("包裹 {Barcode} 上传失败，使用异常口：{ErrorChute}", package.Barcode,
                                chuteSettings.ErrorChuteNumber);
                        }
                        else
                        {
                            // Upload Success
                            Log.Information("成功上传称重数据: {Barcode}", package.Barcode);

                            // Match chute rule
                            var matchedChute = chuteSettings.FindMatchingChute(package.Barcode);

                            if (matchedChute.HasValue)
                            {
                                package.SetChute(matchedChute.Value);
                                Log.Information("包裹 {Barcode} 匹配到格口 {Chute}", package.Barcode, matchedChute.Value);
                                // Set status after successful upload and rule match, indicating data is ready
                                package.SetStatus(Success); // Use MeasureSuccess as an intermediate success status
                            }
                            else
                            {
                                // No Rule Match
                                package.SetChute(chuteSettings.ErrorChuteNumber);
                                package.SetStatus(Error, "未匹配规则");
                                Interlocked.Increment(ref _otherErrorCount);
                                Log.Warning("包裹 {Barcode} 未匹配规则，使用异常口：{ErrorChute}",
                                    package.Barcode, chuteSettings.ErrorChuteNumber);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Upload Exception
                    Log.Error(ex, "上传称重数据时发生错误: {Barcode}{Message}", package.Barcode, ex.Message);
                    _notificationService.ShowError($"上传称重数据异常: {ex.Message}");
                    package.SetChute(chuteSettings.ErrorChuteNumber);
                    package.SetStatus(Error, $"上传异常: {ex.Message}");
                    Interlocked.Increment(ref _otherErrorCount);
                    Log.Warning("包裹 {Barcode} 上传异常，使用异常口：{ErrorChute}", package.Barcode, chuteSettings.ErrorChuteNumber);
                }
            }

            // Process package through sorting service
            _sortService.ProcessPackage(package);
            Interlocked.Increment(ref _totalPackageCount);

            // Update counters and final status
            // Only consider it a success if the status is not Error or Timeout before sending to sort
            if (package.Status != Error && package.Status != PackageStatus.Timeout)
            {
                Interlocked.Increment(ref _successPackageCount);
                // Set final success status (if not already set to an error/timeout)
                package.SetStatus(Success, "分拣成功"); // Or maybe SortSuccess if applicable
                Log.Information("包裹 {Barcode} 最终状态为成功，送往格口 {Chute}", package.Barcode, package.ChuteNumber);
            }
            else
            {
                Interlocked.Increment(ref _failedPackageCount);
                Log.Warning("包裹 {Barcode} 最终状态为 {Status}，送往格口 {Chute}：{ErrorMessage}",
                            package.Barcode, package.StatusDisplay, package.ChuteNumber, package.ErrorMessage ?? "无错误详情");
            }

            // Update UI (Package Info Items)
            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    UpdatePackageInfoItems(package);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "更新UI时发生错误");
                }
            });

            // Update UI (History and Statistics)
            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    PackageHistory.Insert(0, package);
                    while (PackageHistory.Count > 1000)
                        PackageHistory.RemoveAt(PackageHistory.Count - 1);
                    UpdateStatistics();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "更新统计信息时发生错误");
                }
            });

            // Async save to database
            _ = Task.Run(async () =>
            {
                try
                {
                    await _packageDataService.AddPackageAsync(package);
                    Log.Debug("包裹记录已保存到数据库：{Barcode}", package.Barcode);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "保存包裹记录到数据库时发生错误：{Barcode}", package.Barcode);
                    _notificationService.ShowError($"保存包裹记录失败：{ex.Message}");
                }
            });

            // Upload to Waybill Service and wait
            try
            {
                await _waybillUploadService.UploadPackageAndWaitAsync(package);
                Log.Debug("包裹已上传到西逸谷并等待完成：{Barcode}", package.Barcode);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "等待西逸谷上传完成时发生错误：{Barcode}", package.Barcode);
                _notificationService.ShowWarning($"西逸谷上传等待失败：{ex.Message}");
            }
        }
        catch (Exception ex)
        {
            // General Exception during processing
            Log.Error(ex, "处理包裹信息时发生未预料的错误：{Barcode}", package.Barcode);
            {
                // Try to assign to error chute if possible
                var chuteSettings = _settingsService.LoadSettings<ChuteSettings>(); // Reload in case it wasn't loaded
                package.SetChute(chuteSettings.ErrorChuteNumber);
                package.SetStatus(Error, $"处理异常: {ex.Message}");
                Log.Warning("包裹 {Barcode} 处理异常，使用异常口：{ErrorChute}", package.Barcode, chuteSettings.ErrorChuteNumber);
            }
        }
        finally
        {
            package?.ReleaseImage();
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