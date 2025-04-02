using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
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
using DeviceService.DataSourceDevices.Scanner;
using DeviceService.DataSourceDevices.Services;
using FuzhouPolicyForce.WangDianTong;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;
using SharedUI.Models;
using SixLabors.ImageSharp;
using SortingServices.Pendulum;

namespace FuzhouPolicyForce.ViewModels;

internal class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly ICameraService _cameraService;
    private readonly IDialogService _dialogService;
    private readonly IPackageDataService _packageDataService;
    private readonly ISettingsService _settingsService;
    private readonly IPendulumSortService _sortService;
    private readonly List<IDisposable> _subscriptions = [];

    private readonly DispatcherTimer _timer;

    private string _currentBarcode = string.Empty;

    private BitmapSource? _currentImage;

    private int _currentPackageIndex;
    private bool _disposed;

    private readonly IWangDianTongApiService _wangDianTongApiService;

    private SystemStatus _systemStatus = new();

    public MainWindowViewModel(
        IDialogService dialogService,
        ICameraService cameraService,
        ISettingsService settingsService,
        IPendulumSortService sortService,
        PackageTransferService packageTransferService,
        ScannerStartupService scannerStartupService,
        IPackageDataService packageDataService,
        IWangDianTongApiService wangDianTongApiService)
    {
        _dialogService = dialogService;
        _cameraService = cameraService;
        _settingsService = settingsService;
        _sortService = sortService;
        _packageDataService = packageDataService;
        _wangDianTongApiService = wangDianTongApiService;
        scannerStartupService.GetScannerService();

        // 初始化命令
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
                Icon = "Camera",
                StatusColor = "#F44336" // 红色表示未连接
            });
            Log.Debug("已添加相机状态");

            // 获取分拣配置
            var sortConfig = _settingsService.LoadSettings<PendulumSortConfig>();
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

            // 主动查询并更新摆轮分拣服务的设备连接状态
            var deviceStates = _sortService.GetAllDeviceConnectionStates();
            foreach (var (deviceName, isConnected) in deviceStates)
            {
                var deviceStatus = DeviceStatuses.FirstOrDefault(x => x.Name == deviceName);
                if (deviceStatus == null)
                {
                    Log.Warning("未找到设备状态项: {Name}", deviceName);
                    continue;
                }

                deviceStatus.Status = isConnected ? "已连接" : "已断开";
                deviceStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
                Log.Debug("已更新设备初始状态: {Name} -> {Status}", deviceName, deviceStatus.Status);
            }
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
            Label = "条码",
            Value = "--",
            Description = "包裹条码信息",
            Icon = "Barcode24"
        });

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
            var cameraStatus = DeviceStatuses.FirstOrDefault(static s => s.Name == "相机");
            if (cameraStatus == null) return;

            cameraStatus.Status = isConnected ? "已连接" : "已断开";
            cameraStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
        });
    }

    private async void OnPackageInfo(PackageInfo package)
    {
        try
        {
            // 设置包裹序号
            package.Index = Interlocked.Increment(ref _currentPackageIndex);
            Log.Information("收到包裹信息：{Barcode}, 序号：{Index}", package.Barcode, package.Index);

            try
            {
                // 获取格口规则配置
                var chuteSettings = _settingsService.LoadSettings<ChuteSettings>();

                // 判断条码是否为空或noread
                if (string.IsNullOrEmpty(package.Barcode) ||
                    string.Equals(package.Barcode, "noread", StringComparison.OrdinalIgnoreCase))
                {
                    // 使用异常口
                    package.ChuteName = chuteSettings.ErrorChuteNumber;
                    Log.Warning("包裹条码为空或noread，使用异常口：{ErrorChute}", chuteSettings.ErrorChuteNumber);
                }
                else
                {
                    // 尝试匹配格口规则
                    var matchedChute = chuteSettings.FindMatchingChute(package.Barcode);

                    if (matchedChute.HasValue)
                    {
                        package.ChuteName = matchedChute.Value;
                        Log.Information("包裹 {Barcode} 匹配到格口 {Chute}", package.Barcode, matchedChute.Value);
                    }
                    else
                    {
                        // 没有匹配到规则，使用异常口
                        package.ChuteName = chuteSettings.ErrorChuteNumber;
                        Log.Warning("包裹 {Barcode} 未匹配到任何规则，使用异常口：{ErrorChute}",
                            package.Barcode, chuteSettings.ErrorChuteNumber);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "获取格口号时发生错误：{Barcode}", package.Barcode);
                package.SetError($"获取格口号失败：{ex.Message}");
            }

            // 调用网店通API并处理响应
            try
            {
                var response = await _wangDianTongApiService.PushWeightAsync(package.Barcode, (decimal)package.Weight);
                if (!response.IsSuccess)
                {
                    var errorMessage = $"网店通重量回传失败：{response.Message}";
                    Log.Warning("网店通重量回传失败：{Barcode}, 错误码={Code}, 错误信息={Message}",
                        package.Barcode, response.Code, response.Message);
                    package.SetError(errorMessage);
                    package.Information = errorMessage;
                    // 使用异常口
                    package.ChuteName = _settingsService.LoadSettings<ChuteSettings>().ErrorChuteNumber;
                }
                else
                {
                    Log.Information("网店通重量回传成功：{Barcode}, 物流名称={LogisticsName}",
                        package.Barcode, response.LogisticsName);
                    package.Information = $"网店通重量回传成功，物流名称：{response.LogisticsName}";
                }
            }
            catch (Exception ex)
            {
                var errorMessage = $"网店通重量回传失败：{ex.Message}";
                Log.Error(ex, "网店通重量回传时发生错误：{Barcode}", package.Barcode);
                package.SetError(errorMessage);
                package.Information = errorMessage;
                // 使用异常口
                package.ChuteName = _settingsService.LoadSettings<ChuteSettings>().ErrorChuteNumber;
            }

            _sortService.ProcessPackage(package);

            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    CurrentBarcode = package.Barcode;
                    UpdatePackageInfoItems(package);
                    // 6. 更新统计信息和历史包裹列表
                    PackageHistory.Insert(0, package);
                    while (PackageHistory.Count > 1000) // 保持最近1000条记录
                    {
                        var removedPackage = PackageHistory[^1];
                        PackageHistory.RemoveAt(PackageHistory.Count - 1);
                        removedPackage.Dispose(); // 释放被移除的包裹
                    }

                    // 更新统计数据
                    UpdateStatistics();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "更新UI时发生错误");
                }
            });

            // 保存包裹记录到数据库
            try
            {
                await _packageDataService.AddPackageAsync(package);
                Log.Information("包裹记录已保存到数据库：{Barcode}", package.Barcode);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存包裹记录到数据库时发生错误：{Barcode}", package.Barcode);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹信息时发生错误：{Barcode}", package.Barcode);
            package.SetError($"处理失败：{ex.Message}");
        }
        finally
        {
            package.Dispose(); // 确保包裹被释放
        }
    }

    private void UpdatePackageInfoItems(PackageInfo package)
    {
        var barcodeItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "条码");
        if (barcodeItem != null)
        {
            barcodeItem.Value = package.Barcode;
            barcodeItem.Description = string.Equals(package.Barcode, "noread", StringComparison.OrdinalIgnoreCase)
                ? "条码识别失败"
                : "包裹条码信息";
        }

        var weightItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "重量");
        if (weightItem != null)
        {
            weightItem.Value = package.Weight.ToString(CultureInfo.InvariantCulture);
            weightItem.Unit = "kg";
        }

        var sizeItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "尺寸");
        if (sizeItem != null)
        {
            sizeItem.Value = package.VolumeDisplay;
        }

        var segmentItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "段码");
        if (segmentItem != null)
        {
            segmentItem.Value = package.SegmentCode;
            segmentItem.Description = string.IsNullOrEmpty(package.SegmentCode) ? "等待获取..." : "三段码信息";
        }

        var chuteItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "分拣口");
        if (chuteItem != null)
        {
            chuteItem.Value = package.ChuteName.ToString();
            chuteItem.Description = string.IsNullOrEmpty(package.ChuteName.ToString()) ? "等待分配..." : "目标分拣位置";
        }

        var timeItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "时间");
        if (timeItem != null)
        {
            timeItem.Value = package.CreateTime.ToString("HH:mm:ss");
            timeItem.Description = $"处理于 {package.CreateTime:yyyy-MM-dd}";
        }

        var processingTimeItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "处理时间");
        if (processingTimeItem == null) return;

        processingTimeItem.Value = $"{package.ProcessingTime:F0}";
        processingTimeItem.Description = $"耗时 {package.ProcessingTime:F0} 毫秒";
    }

    private void UpdateStatistics()
    {
        var totalItem = StatisticsItems.FirstOrDefault(static x => x.Label == "总包裹数");
        if (totalItem != null)
        {
            totalItem.Value = _currentPackageIndex.ToString();
            totalItem.Description = $"累计处理 {_currentPackageIndex} 个包裹";
        }

        var errorItem = StatisticsItems.FirstOrDefault(static x => x.Label == "异常数");
        if (errorItem != null)
        {
            var errorCount = PackageHistory.Count(static p => !string.IsNullOrEmpty(p.ErrorMessage));
            errorItem.Value = errorCount.ToString();
            errorItem.Description = $"共有 {errorCount} 个异常包裹";
        }

        var efficiencyItem = StatisticsItems.FirstOrDefault(static x => x.Label == "预测效率");
        if (efficiencyItem != null)
        {
            // 获取最近30秒内的包裹
            var thirtySecondsAgo = DateTime.Now.AddSeconds(-30);
            var recentPackages = PackageHistory.Where(p => p.CreateTime > thirtySecondsAgo).ToList();

            if (recentPackages.Count > 0)
            {
                // 计算30秒内的平均处理速度（个/秒）
                var packagesPerSecond = recentPackages.Count / 30.0;
                // 转换为每小时处理量
                var hourlyRate = (int)(packagesPerSecond * 3600);
                efficiencyItem.Value = hourlyRate.ToString();
                efficiencyItem.Description = $"基于最近{recentPackages.Count}个包裹的处理速度";
            }
            else
            {
                efficiencyItem.Value = "0";
                efficiencyItem.Description = "暂无处理数据";
            }
        }

        var avgTimeItem = StatisticsItems.FirstOrDefault(static x => x.Label == "平均处理时间");
        if (avgTimeItem == null) return;

        {
            var recentPackages = PackageHistory.Take(100).ToList();
            if (recentPackages.Count != 0)
            {
                var avgTime = recentPackages.Average(static p => p.ProcessingTime);
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

                // 取消订阅事件
                _sortService.DeviceConnectionStatusChanged -= OnDeviceConnectionStatusChanged;
                _cameraService.ConnectionChanged -= OnCameraConnectionChanged;
                // 释放订阅
                foreach (var subscription in _subscriptions) subscription.Dispose();
                _subscriptions.Clear();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "释放资源时发生错误");
            }

        _disposed = true;
    }
}