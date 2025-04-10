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
using System.IO;
using DeviceService.DataSourceDevices.Camera.Models.Camera;

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
        _dialogService.ShowDialog("HistoryDialog");
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
            if (package.Weight <= 0)
            {
                var weighingSettings = _settingsService.LoadSettings<WeighingSettings>();
                if (weighingSettings.DefaultWeight > 0)
                {
                    package.SetWeight((double)weighingSettings.DefaultWeight);
                    Log.Information("包裹 {Barcode} 重量为0或无效，使用预设重量：{DefaultWeight}kg", package.Barcode, weighingSettings.DefaultWeight);
                }
                else
                {
                    Log.Warning("包裹 {Barcode} 重量为0，且未设置预设重量", package.Barcode);
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
                var chuteSettings = _settingsService.LoadSettings<ChuteSettings>();
                package.SetChute(chuteSettings.ErrorChuteNumber);
                package.SetStatus(PackageStatus.Error, "未识别包裹");
                Log.Warning("包裹条码为空或noread，直接使用异常口：{ErrorChute}", chuteSettings.ErrorChuteNumber);
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

                    // 创建上传任务和超时任务
                    var uploadTask = _weighingService.SendWeightDataAsync(request);
                    var timeoutTask = Task.Delay(500); // 设置500ms超时
                    
                    // 等待任一任务完成
                    var completedTask = await Task.WhenAny(uploadTask, timeoutTask);
                    
                    if (completedTask == timeoutTask)
                    {
                        // 上传超时
                        Log.Warning("上传称重数据超时: {Barcode}", package.Barcode);
                        _notificationService.ShowWarning($"上传称重数据超时: {package.Barcode}");
                        
                        // 分配到异常口
                        var chuteSettings = _settingsService.LoadSettings<ChuteSettings>();
                        package.SetChute(chuteSettings.ErrorChuteNumber);
                        package.SetStatus(PackageStatus.Timeout, "响应超时");
                        Log.Warning("包裹 {Barcode} 上传超时，使用异常口：{ErrorChute}", package.Barcode,
                            chuteSettings.ErrorChuteNumber);
                    }
                    else
                    {
                        // 上传完成，获取结果
                        var response = await uploadTask;
                        if (!response.Success)
                        {
                            Log.Warning("上传称重数据失败: Code={Code}, Message={Message}", response.Code, response.Message);
                            _notificationService.ShowWarning($"上传称重数据失败: {response.Message}");

                            // 上传失败，分配到异常口
                            var chuteSettings = _settingsService.LoadSettings<ChuteSettings>();
                            package.SetChute(chuteSettings.ErrorChuteNumber);
                            package.SetStatus(PackageStatus.Error, $"{response.Message}");
                            Log.Warning("包裹 {Barcode} 上传失败，使用异常口：{ErrorChute}", package.Barcode,
                                chuteSettings.ErrorChuteNumber);
                        }
                        else
                        {
                            Log.Information("成功上传称重数据: {Barcode}", package.Barcode);

                            // 上传成功，根据规则匹配格口
                            var chuteSettings = _settingsService.LoadSettings<ChuteSettings>();

                            // 尝试匹配格口规则
                            var matchedChute = chuteSettings.FindMatchingChute(package.Barcode);

                            if (matchedChute.HasValue)
                            {
                                package.SetChute(matchedChute.Value);
                                package.SetStatus(PackageStatus.WaitingForChute);
                                Log.Information("包裹 {Barcode} 匹配到格口 {Chute}", package.Barcode, matchedChute.Value);
                            }
                            else
                            {
                                // 没有匹配到规则，使用异常口
                                package.SetChute(chuteSettings.ErrorChuteNumber);
                                package.SetStatus(PackageStatus.Error, "未匹配到格口规则");
                                Log.Warning("包裹 {Barcode} 未匹配到任何规则，使用异常口：{ErrorChute}",
                                    package.Barcode, chuteSettings.ErrorChuteNumber);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "上传称重数据时发生错误: {Barcode}", package.Barcode);
                    _notificationService.ShowError($"上传称重数据失败: {ex.Message}");

                    // 发生异常，分配到异常口
                    var chuteSettings = _settingsService.LoadSettings<ChuteSettings>();
                    package.SetChute(chuteSettings.ErrorChuteNumber);
                    package.SetStatus(PackageStatus.Error, $"上传称重数据失败：{ex.Message}");
                }
            }
            // 第五步：添加到分拣服务
            _sortService.ProcessPackage(package);
            Interlocked.Increment(ref _totalPackageCount);
            
            // 只有包裹状态正常才计入成功统计和设置分拣成功状态
            if (package.Status != PackageStatus.Error && package.Status != PackageStatus.Timeout && string.IsNullOrEmpty(package.ErrorMessage))
            {
                Interlocked.Increment(ref _successPackageCount);
                // 设置分拣成功状态
                package.SetStatus(PackageStatus.SortSuccess, "分拣成功");
                Log.Information("包裹 {Barcode} 分拣成功", package.Barcode);
            }
            else
            {
                Interlocked.Increment(ref _failedPackageCount);
                // 保留原有异常信息，不做状态修改
                Log.Warning("包裹 {Barcode} 保持状态 {Status}：{ErrorMessage}", package.Barcode, package.StatusDisplay, package.ErrorMessage ?? "无错误详情");
            }
            
            // 第六步：更新UI相关信息
            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    // 更新实时包裹数据
                    UpdatePackageInfoItems(package);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "更新UI时发生错误");
                }
            });

            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    // 更新历史记录
                    PackageHistory.Insert(0, package);
                    while (PackageHistory.Count > 1000) // 保持最近1000条记录
                        PackageHistory.RemoveAt(PackageHistory.Count - 1);
                    // 更新统计数据
                    UpdateStatistics();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "更新统计信息时发生错误");
                }
            });

            // 异步保存到数据库，不等待完成
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
            
            // 将包裹信息上传到西逸谷，不等待返回
            try
            {
                // 异步上传到西逸谷，不等待完成
                _waybillUploadService.EnqueuePackage(package);
                Log.Debug("包裹已加入西逸谷上传队列：{Barcode}", package.Barcode);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "将包裹加入西逸谷上传队列时发生错误：{Barcode}", package.Barcode);
                _notificationService.ShowWarning($"西逸谷上传队列错误：{ex.Message}");
            }
            
            // 异步保存包裹图片到本地，不等待完成
            _ = Task.Run(async () =>
            {
                try
                {
                    var cameraSettings = _settingsService.LoadSettings<CameraSettings>();
                    if (!cameraSettings.EnableImageSaving || package.Image == null) return;
                    
                    // 确定包裹类型并选择相应的子文件夹
                    string packageTypeFolder;
                    var barcode = string.IsNullOrEmpty(package.Barcode) ? "noread" : package.Barcode;
                    
                    if (string.Equals(barcode, "noread", StringComparison.OrdinalIgnoreCase))
                    {
                        packageTypeFolder = "NoRead";
                    }
                    else if (barcode.Contains("-"))
                    {
                        packageTypeFolder = "MultiCode";
                    }
                    else
                    {
                        packageTypeFolder = "Normal";
                    }
                    
                    // 构建子目录结构 {类型}/{yyyy}/{MM}/{dd}/{hh}
                    var now = DateTime.Now;
                    var subDirPath = Path.Combine(
                        cameraSettings.ImageSavePath,
                        packageTypeFolder,
                        now.ToString("yyyy"),
                        now.ToString("MM"),
                        now.ToString("dd"),
                        now.ToString("HH"));
                    
                    // 确保目录存在
                    if (!Directory.Exists(subDirPath))
                    {
                        Directory.CreateDirectory(subDirPath);
                    }
                    
                    // 构建图片名称：条码_时间戳.格式
                    var timestamp = now.ToString("yyyyMMddHHmmss");
                    var extension = cameraSettings.ImageFormat.ToString().ToLower();
                    var fileName = $"{barcode}_{timestamp}.{extension}";
                    
                    // 完整图片路径
                    var fullPath = Path.Combine(subDirPath, fileName);
                    
                    // 保存图片
                    await using var fileStream = new FileStream(fullPath, FileMode.Create);
                    BitmapEncoder encoder = cameraSettings.ImageFormat switch
                    {
                        DeviceService.DataSourceDevices.Camera.Models.Camera.Enums.ImageFormat.Jpeg => new JpegBitmapEncoder(),
                        DeviceService.DataSourceDevices.Camera.Models.Camera.Enums.ImageFormat.Png => new PngBitmapEncoder(),
                        DeviceService.DataSourceDevices.Camera.Models.Camera.Enums.ImageFormat.Bmp => new BmpBitmapEncoder(),
                        DeviceService.DataSourceDevices.Camera.Models.Camera.Enums.ImageFormat.Tiff => new TiffBitmapEncoder(),
                        _ => new JpegBitmapEncoder()
                    };
                    
                    encoder.Frames.Add(BitmapFrame.Create(package.Image));
                    encoder.Save(fileStream);
                    
                    Log.Debug("包裹 {Barcode} 图片已保存至：{Path}", barcode, fullPath);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "保存包裹图片时发生错误：{Barcode}", package.Barcode);
                    // 在后台线程中不应该直接调用UI相关方法，使用调度器
                    Application.Current.Dispatcher.Invoke(() => 
                    {
                        _notificationService.ShowWarning($"保存图片失败：{ex.Message}");
                    });
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹信息时发生错误：{Barcode}", package.Barcode);
            package.SetStatus(PackageStatus.Error, $"处理失败：{ex.Message}");
        }finally
        {
            // 释放图片资源
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
            PackageStatus.SortSuccess or PackageStatus.MeasureSuccess or PackageStatus.WeighSuccess or PackageStatus.LoadingSuccess 
                => "#4CAF50", // Green for success
            PackageStatus.Error or PackageStatus.Timeout or PackageStatus.MeasureFailed or PackageStatus.WeighFailed or PackageStatus.SortFailed or PackageStatus.LoadingRejected 
                => "#F44336", // Red for error/timeout/failure
            _ => "#2196F3"   // Blue for other states (e.g., processing, waiting)
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

        var failedItem = StatisticsItems.FirstOrDefault(static x => x.Label == "失败数");
        if (failedItem != null)
        {
            // Use the persistent counter
            failedItem.Value = _failedPackageCount.ToString(); 
            failedItem.Description = $"失败处理 {_failedPackageCount} 个包裹";
        }

        var rateItem = StatisticsItems.FirstOrDefault(static x => x.Label == "处理速率");
        if (rateItem == null) return;

        {
            var minuteAgo = DateTime.Now.AddMinutes(-1);
            var lastMinuteCount = PackageHistory.Count(p => p.CreateTime > minuteAgo);
            var hourlyRate = lastMinuteCount * 60; // 将每分钟的处理量转换为每小时，并转为整数
            rateItem.Value = hourlyRate.ToString();
            rateItem.Description = $"预计每小时处理 {hourlyRate} 个";
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
                    Application.Current.Dispatcher.Invoke(() => _timer.Stop());
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