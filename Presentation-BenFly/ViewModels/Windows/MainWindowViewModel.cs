using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommonLibrary.Models;
using CommonLibrary.Models.Settings.Sort;
using CommonLibrary.Services;
using DeviceService;
using DeviceService.Camera;
using Presentation_BenFly.Services;
using Presentation_BenFly.Services.Sortings.Interfaces;
using Presentation_CommonLibrary.Models;
using Presentation_CommonLibrary.Services;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;
using SixLabors.ImageSharp;

namespace Presentation_BenFly.ViewModels.Windows;

public class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly BenNiaoPackageService _benNiaoService;
    private readonly ICameraService _cameraService;
    private readonly ICustomDialogService _dialogService;
    private readonly ISettingsService _settingsService;
    private readonly IPendulumSortService _sortService;
    private readonly List<IDisposable> _subscriptions = [];

    private readonly DispatcherTimer _timer;

    private string _currentBarcode = string.Empty;

    private BitmapSource? _currentImage;

    private int _currentPackageIndex;
    private bool _disposed;

    private bool _isInitialized;

    private SystemStatus _systemStatus = new();

    public MainWindowViewModel(
        ICustomDialogService dialogService,
        ICameraService cameraService,
        ISettingsService settingsService,
        IPendulumSortService sortService,
        PackageTransferService packageTransferService,
        BenNiaoPackageService benNiaoService)
    {
        _dialogService = dialogService;
        _cameraService = cameraService;
        _settingsService = settingsService;
        _sortService = sortService;
        _benNiaoService = benNiaoService;

        // 初始化命令
        OpenSettingsCommand = new DelegateCommand(ExecuteOpenSettings);

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

        // 订阅分拣设备连接事件
        _sortService.DeviceConnectionStatusChanged += OnDeviceConnectionStatusChanged;

        // 订阅相机连接状态事件
        _cameraService.ConnectionChanged += OnCameraConnectionChanged;

        // 订阅包裹流
        _subscriptions.Add(packageTransferService.PackageStream
            .ObserveOn(Scheduler.CurrentThread)
            .Subscribe(OnPackageInfo));

        // 订阅图像流
        _subscriptions.Add(_cameraService.ImageStream
            .ObserveOn(TaskPoolScheduler.Default) // 使用任务池调度器
            .Subscribe(imageData =>
            {
                try
                {
                    var image = imageData.image;

                    // 使用 TaskPool 进行图像编码
                    Task.Run(() =>
                    {
                        try
                        {
                            using var memoryStream = new MemoryStream();
                            image.SaveAsPng(memoryStream);
                            memoryStream.Position = 0;
                            var imageBytes = memoryStream.ToArray();

                            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
                            {
                                try
                                {
                                    var bitmap = new BitmapImage();
                                    bitmap.BeginInit();
                                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                                    bitmap.StreamSource = new MemoryStream(imageBytes);
                                    bitmap.EndInit();
                                    bitmap.Freeze(); // 使图像可以跨线程访问

                                    // 更新UI
                                    CurrentImage = bitmap;
                                }
                                catch (Exception ex)
                                {
                                    Log.Error(ex, "创建BitmapImage时发生错误");
                                }
                            });
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "编码图像数据时发生错误");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理图像数据时发生错误");
                }
            }));

        // 初始化摆轮分拣服务
        _ = InitializeSortServiceAsync();
    }

    public DelegateCommand OpenSettingsCommand { get; }

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
                Icon = "Camera",
                StatusColor = "#F44336" // 红色表示未连接
            });
            Log.Debug("已添加相机状态");

            // 获取分拣配置
            var sortConfig = _settingsService.LoadConfiguration<SortConfiguration>();
            Log.Debug("已加载分拣配置，光电数量: {Count}", sortConfig.SortingPhotoelectrics.Count);

            // 添加触发光电状态
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "触发光电",
                Status = "未连接",
                Icon = "RadioTower",
                StatusColor = "#F44336"
            });
            Log.Debug("已添加触发光电状态");

            // 添加分检光电状态
            foreach (var photoelectric in sortConfig.SortingPhotoelectrics)
            {
                DeviceStatuses.Add(new DeviceStatus
                {
                    Name = photoelectric.Name,
                    Status = "未连接",
                    Icon = "RadioTower",
                    StatusColor = "#F44336"
                });
                Log.Debug("已添加分拣光电状态: {Name}", photoelectric.Name);
            }

            Log.Information("设备状态列表初始化完成，共 {Count} 个设备", DeviceStatuses.Count);
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
            Icon = "CubeOutline24"
        });

        StatisticsItems.Add(new StatisticsItem
        {
            Label = "异常数",
            Value = "0",
            Unit = "个",
            Description = "处理异常的包裹数量",
            Icon = "AlertOutline24"
        });

        StatisticsItems.Add(new StatisticsItem
        {
            Label = "预测效率",
            Value = "0",
            Unit = "个/小时",
            Description = "预计每小时处理量",
            Icon = "TrendingUp24"
        });

        StatisticsItems.Add(new StatisticsItem
        {
            Label = "平均处理时间",
            Value = "0",
            Unit = "ms",
            Description = "单个包裹平均处理时间",
            Icon = "TimerOutline24"
        });
    }

    private void InitializePackageInfoItems()
    {
        PackageInfoItems.Add(new PackageInfoItem
        {
            Label = "重量",
            Value = "--",
            Unit = "kg",
            Description = "包裹重量",
            Icon = "ScaleBalance24"
        });

        PackageInfoItems.Add(new PackageInfoItem
        {
            Label = "尺寸",
            Value = "--",
            Unit = "cm",
            Description = "长×宽×高",
            Icon = "RulerSquare24"
        });

        PackageInfoItems.Add(new PackageInfoItem
        {
            Label = "段码",
            Value = "--",
            Description = "三段码信息",
            Icon = "Barcode24"
        });

        PackageInfoItems.Add(new PackageInfoItem
        {
            Label = "分拣口",
            Value = "--",
            Description = "目标分拣位置",
            Icon = "ArrowSplitHorizontal24"
        });

        PackageInfoItems.Add(new PackageInfoItem
        {
            Label = "处理时间",
            Value = "--",
            Unit = "ms",
            Description = "系统处理耗时",
            Icon = "Timer24"
        });

        PackageInfoItems.Add(new PackageInfoItem
        {
            Label = "时间",
            Value = "--:--:--",
            Description = "包裹处理时间",
            Icon = "Clock24"
        });
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

    private void OnCameraConnectionChanged(string cameraId, bool isConnected)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var cameraStatus = DeviceStatuses.FirstOrDefault(s => s.Name == "相机");
            if (cameraStatus == null) return;

            cameraStatus.Status = isConnected ? "已连接" : "已断开";
            cameraStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
        });
    }

    private async Task InitializeSortServiceAsync()
    {
        try
        {
            if (_isInitialized) return;

            Log.Information("正在初始化摆轮分拣服务...");

            // 加载分拣配置
            var configuration = _settingsService.LoadSettings<SortConfiguration>("SortConfiguration");

            // 初始化分拣服务
            await _sortService.InitializeAsync(configuration);
            Log.Information("摆轮分拣服务初始化完成");

            // 启动分拣服务
            await _sortService.StartAsync();
            Log.Information("摆轮分拣服务已启动");

            // 更新设备状态
            await UpdateDeviceStatusesAsync();
            Log.Information("设备状态已更新");

            _isInitialized = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化摆轮分拣服务时发生错误");
            throw;
        }
    }

    private async void OnPackageInfo(PackageInfo package)
    {
        try
        {
            // 设置包裹序号
            package.Index = Interlocked.Increment(ref _currentPackageIndex);
            Log.Information("收到包裹信息：{Barcode}, 序号：{Index}", package.Barcode, package.Index);
            _sortService.ProcessPackage(package);
            // 1. 通过笨鸟系统服务获取三段码并处理上传
            var benNiaoResult = await _benNiaoService.ProcessPackageAsync(package);
            if (!benNiaoResult)
            {
                Log.Warning("笨鸟系统处理包裹失败：{Barcode}", package.Barcode);
                package.SetError("笨鸟系统处理失败");
            }

            // 笨鸟系统处理完成后释放图像资源
            if (package.Image != null)
            {
                package.Image.Dispose();
                package.Image = null;
                Log.Debug("已释放包裹 {Barcode} 的图像资源", package.Barcode);
            }

            try
            {
                var chuteConfig = _settingsService.LoadConfiguration<ChuteConfiguration>();
                var chute = chuteConfig.GetChuteBySpaceSeparatedSegments(package.SegmentCode);
                package.ChuteName = chute;
                Log.Information("包裹 {Barcode} 分配到格口 {Chute}，段码：{SegmentCode}",
                    package.Barcode, chute, package.SegmentCode);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "获取格口号时发生错误：{Barcode}, {SegmentCode}",
                    package.Barcode, package.SegmentCode);
                package.SetError($"获取格口号失败：{ex.Message}");
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    CurrentBarcode = package.Barcode;
                    UpdatePackageInfoItems(package);
                    // 6. 更新统计信息和历史包裹列表
                    PackageHistory.Insert(0, package);
                    while (PackageHistory.Count > 1000) // 保持最近1000条记录
                        PackageHistory.RemoveAt(PackageHistory.Count - 1);

                    // 更新统计数据
                    UpdateStatistics();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "更新UI时发生错误");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹信息时发生错误：{Barcode}", package.Barcode);
            package.SetError($"处理失败：{ex.Message}");
        }
    }

    private void UpdatePackageInfoItems(PackageInfo package)
    {
        var weightItem = PackageInfoItems.FirstOrDefault(x => x.Label == "重量");
        if (weightItem != null)
        {
            weightItem.Value = package.Weight.ToString(CultureInfo.InvariantCulture);
            weightItem.Unit = "kg";
        }

        var sizeItem = PackageInfoItems.FirstOrDefault(x => x.Label == "尺寸");
        if (sizeItem != null) sizeItem.Value = package.VolumeDisplay;

        var segmentItem = PackageInfoItems.FirstOrDefault(x => x.Label == "段码");
        if (segmentItem != null)
        {
            segmentItem.Value = package.SegmentCode;
            segmentItem.Description = string.IsNullOrEmpty(package.SegmentCode) ? "等待获取..." : "三段码信息";
        }

        var chuteItem = PackageInfoItems.FirstOrDefault(x => x.Label == "分拣口");
        if (chuteItem != null)
        {
            chuteItem.Value = package.ChuteName.ToString();
            chuteItem.Description = string.IsNullOrEmpty(package.ChuteName.ToString()) ? "等待分配..." : "目标分拣位置";
        }

        var timeItem = PackageInfoItems.FirstOrDefault(x => x.Label == "时间");
        if (timeItem != null)
        {
            timeItem.Value = package.CreateTime.ToString("HH:mm:ss");
            timeItem.Description = $"处理于 {package.CreateTime:yyyy-MM-dd}";
        }

        var processingTimeItem = PackageInfoItems.FirstOrDefault(x => x.Label == "处理时间");
        if (processingTimeItem == null) return;
        processingTimeItem.Value = $"{package.ProcessingTime:F0}";
        processingTimeItem.Description = $"耗时 {package.ProcessingTime:F0} 毫秒";
    }

    private void UpdateStatistics()
    {
        var totalItem = StatisticsItems.FirstOrDefault(x => x.Label == "总包裹数");
        if (totalItem != null)
        {
            totalItem.Value = PackageHistory.Count.ToString();
            totalItem.Description = $"累计处理 {PackageHistory.Count} 个包裹";
        }

        var errorItem = StatisticsItems.FirstOrDefault(x => x.Label == "异常数");
        if (errorItem != null)
        {
            var errorCount = PackageHistory.Count(p => !string.IsNullOrEmpty(p.ErrorMessage));
            errorItem.Value = errorCount.ToString();
            errorItem.Description = $"共有 {errorCount} 个异常包裹";
        }

        var efficiencyItem = StatisticsItems.FirstOrDefault(x => x.Label == "预测效率");
        if (efficiencyItem != null)
        {
            var hourAgo = DateTime.Now.AddHours(-1);
            var hourlyCount = PackageHistory.Count(p => p.CreateTime > hourAgo);
            efficiencyItem.Value = hourlyCount.ToString();
            efficiencyItem.Description = $"最近一小时处理 {hourlyCount} 个";
        }

        var avgTimeItem = StatisticsItems.FirstOrDefault(x => x.Label == "平均处理时间");
        if (avgTimeItem == null) return;
        {
            var recentPackages = PackageHistory.Take(100).ToList();
            if (recentPackages.Count != 0)
            {
                var avgTime = recentPackages.Average(p => p.ProcessingTime);
                avgTimeItem.Value = avgTime.ToString("F0");
                avgTimeItem.Description = $"最近{recentPackages.Count}个包裹平均耗时";
            }
            else
            {
                avgTimeItem.Value = "0";
                avgTimeItem.Description = "暂无处理数据";
            }
        }
    }

    /// <summary>
    ///     更新设备连接状态
    /// </summary>
    private Task UpdateDeviceStatusesAsync()
    {
        try
        {
            // 更新相机状态
            var cameraStatus = DeviceStatuses.FirstOrDefault(x => x.Name == "相机");
            if (cameraStatus != null)
            {
                var isConnected = _cameraService.IsConnected;
                cameraStatus.Status = isConnected ? "已连接" : "已断开";
                cameraStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
            }

            // 更新分拣设备状态
            if (_sortService.IsRunning())
            {
                // 获取所有设备的连接状态
                var deviceStates = _sortService.GetAllDeviceConnectionStates();

                // 更新触发光电状态
                var triggerStatus = DeviceStatuses.FirstOrDefault(x => x.Name == "触发光电");
                if (triggerStatus != null && deviceStates.TryGetValue("触发光电", out var triggerConnected))
                {
                    triggerStatus.Status = triggerConnected ? "已连接" : "已断开";
                    triggerStatus.StatusColor = triggerConnected ? "#4CAF50" : "#F44336";
                }

                // 更新分检光电状态
                foreach (var status in DeviceStatuses.Where(x => x.Name != "相机" && x.Name != "触发光电"))
                {
                    if (!deviceStates.TryGetValue(status.Name, out var isConnected)) continue;
                    status.Status = isConnected ? "已连接" : "已断开";
                    status.StatusColor = isConnected ? "#4CAF50" : "#F44336";

                    Log.Debug("更新分拣光电状态: {Name} -> {Status}",
                        status.Name,
                        isConnected ? "已连接" : "已断开");
                }
            }
            else
            {
                // 如果服务未运行，将所有设备状态设置为未连接
                foreach (var status in DeviceStatuses.Where(x => x.Name != "相机"))
                {
                    status.Status = "未连接";
                    status.StatusColor = "#F44336";
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新设备状态时发生错误");
        }

        return Task.CompletedTask;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
            try
            {
                // 停止分拣服务
                if (_sortService.IsRunning())
                {
                    _sortService.StopAsync().Wait();
                    Log.Information("摆轮分拣服务已停止");
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
                foreach (var subscription in _subscriptions) subscription.Dispose();

                _subscriptions.Clear();

                // 停止定时器
                _timer.Stop();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "释放资源时发生错误");
            }

        _disposed = true;
    }
}