using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Common.Models.Package;
using Common.Models.Settings.ChuteRules;
using Common.Models.Settings.Sort.PendulumSort;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Scanner;
using DeviceService.DataSourceDevices.Services;
using Serilog;
using SharedUI.Models;
using SortingServices.Pendulum;
using SortingServices.Servers.Services.JuShuiTan;

namespace ChongqingYekelai.ViewModels;

internal class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly ICameraService _cameraService;
    private readonly IDialogService _dialogService;
    private readonly IJuShuiTanService _juShuiTanService;
    private readonly ISettingsService _settingsService;
    private readonly IPendulumSortService _sortService;
    private readonly List<IDisposable> _subscriptions = [];

    private readonly DispatcherTimer _timer;

    private string _currentBarcode = string.Empty;

    private BitmapSource? _currentImage;

    private bool _disposed;

    private SystemStatus _systemStatus = new();

    public MainWindowViewModel(
        IDialogService dialogService,
        ICameraService cameraService,
        ISettingsService settingsService,
        IPendulumSortService sortService,
        IJuShuiTanService juShuiTanService,
        PackageTransferService packageTransferService,
        ScannerStartupService scannerStartupService)
    {
        _dialogService = dialogService;
        _cameraService = cameraService;
        _settingsService = settingsService;
        _sortService = sortService;
        _juShuiTanService = juShuiTanService;
        scannerStartupService.GetScannerService();

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
            .ObserveOn(Scheduler.CurrentThread) // 直接在 UI 线程观察
            .Subscribe(imageData => // imageData is a tuple (BitmapSource image, IReadOnlyList<BarcodeLocation> barcodes)
            {
                try
                {
                    // 确保 BitmapSource 可以在 UI 线程之外访问（如果需要跨线程访问）
                    if (imageData is { CanFreeze: true, IsFrozen: false })
                    {
                        imageData.Freeze();
                    }
                    // 更新UI
                    CurrentImage = imageData;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理 BitmapSource 或更新 UI 时发生错误");
                }
            },
            ex => Log.Error(ex, "处理图像流时发生未处理的异常"))); // 添加错误处理
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
                Icon = "相机模块",
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
        StatisticsItems.Add(new StatisticsItem(
            label: "总包裹数",
            value: "0",
            unit: "个",
            description: "累计处理包裹总数",
            icon: "CubeOutline24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            label: "异常数",
            value: "0",
            unit: "个",
            description: "处理异常的包裹数量",
            icon: "AlertOutline24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            label: "预测效率",
            value: "0",
            unit: "个/小时",
            description: "预计每小时处理量",
            icon: "TrendingUp24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            label: "平均处理时间",
            value: "0",
            unit: "ms",
            description: "单个包裹平均处理时间",
            icon: "TimerOutline24"
        ));
    }

    private void InitializePackageInfoItems()
    {

        PackageInfoItems.Add(new PackageInfoItem(
            label: "重量",
            value: "--",
            unit: "kg",
            description: "包裹重量",
            icon: "ScaleBalance24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            label: "尺寸",
            value: "--",
            unit: "cm",
            description: "长×宽×高",
            icon: "RulerSquare24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            label: "分拣口",
            value: "--",
            description: "目标分拣位置",
            icon: "ArrowSplitHorizontal24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            label: "处理时间",
            value: "--",
            unit: "ms",
            description: "系统处理耗时",
            icon: "Timer24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            label: "时间",
            value: "--:--:--",
            description: "包裹处理时间",
            icon: "Clock24"
        ));
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

    private void OnCameraConnectionChanged(string? cameraId, bool isConnected)
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
            // 获取格口规则配置
            var chuteSettings = _settingsService.LoadSettings<ChuteSettings>();
            // 判断条码是否为空或noread
            if (string.IsNullOrEmpty(package.Barcode) ||
                string.Equals(package.Barcode, "noread", StringComparison.OrdinalIgnoreCase))
            {
                // 使用异常口
                package.SetChute(chuteSettings.ErrorChuteNumber);
                package.SetStatus(PackageStatus.Failed, "条码为空或noread");
                Log.Warning("包裹条码为空或noread，使用异常口：{ErrorChute}", chuteSettings.ErrorChuteNumber);
            }
            else
            {
                // 尝试匹配格口规则
                var matchedChute = chuteSettings.FindMatchingChute(package.Barcode);

                if (matchedChute.HasValue)
                {
                    package.SetChute(matchedChute.Value);
                    Log.Information("包裹 {Barcode} 匹配到格口 {Chute}", package.Barcode, matchedChute.Value);
                }
                else
                {
                    // 没有匹配到规则，使用异常口
                    package.SetChute(chuteSettings.ErrorChuteNumber);
                    package.SetStatus(PackageStatus.Failed, "未匹配到格口规则");
                    Log.Warning("包裹 {Barcode} 未匹配到任何规则，使用异常口：{ErrorChute}",
                        package.Barcode, chuteSettings.ErrorChuteNumber);
                }
            }
            
            _sortService.ProcessPackage(package);
            // 上传包裹数据到聚水潭
            try
            {
                // 如果条码为空或noread，不上传数据
                if (string.IsNullOrEmpty(package.Barcode) ||
                    string.Equals(package.Barcode, "noread", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warning("包裹条码为空或noread，跳过上传到聚水潭");
                    return;
                }

                var request = new WeightSendRequest
                {
                    LogisticsId = package.Barcode,
                    Weight = Convert.ToDecimal(package.Weight),
                    IsUnLid = false, // 根据实际情况设置
                    Type = 5,
                };

                Log.Debug("准备上传包裹数据到聚水潭: {@Request}", request);
                var response = await _juShuiTanService.WeightAndSendAsync(request);

                if (response.Code == 0)
                {
                    Log.Information("包裹 {Barcode} 数据已成功上传到聚水潭", package.Barcode);

                    // 检查返回的数据
                    if (response.Data.Items.Count > 0)
                    {
                        var data = response.Data.Items[0];
                        if (data.Code != 0)
                        {
                            Log.Warning("包裹 {Barcode} 数据上传到聚水潭返回错误: {ErrorMessage}",
                                package.Barcode, data.Message);
                            package.SetStatus(PackageStatus.Error,$"{data.Message}");
                            package.SetChute(_settingsService.LoadSettings<ChuteSettings>().ErrorChuteNumber);
                        }
                        else
                        {
                            Log.Debug("包裹 {Barcode} 上传成功，物流公司：{Company}，运单号：{TrackingNumber}",
                                package.Barcode, data.LogisticsCompany, data.LogisticsId);
                            package.SetStatus(PackageStatus.Success);
                        }
                    }
                }
                else
                {
                    Log.Warning("包裹 {Barcode} 数据上传到聚水潭失败: {ErrorCode} - {ErrorMessage}",
                        package.Barcode, response.Code, response.Message);
                    package.SetStatus(PackageStatus.Error,$"{response.Message}");
                    package.SetChute(_settingsService.LoadSettings<ChuteSettings>().ErrorChuteNumber);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "上传包裹 {Barcode} 数据到聚水潭时发生错误", package.Barcode);
                package.SetStatus(PackageStatus.Error,$"{ex.Message}");
                package.SetChute(_settingsService.LoadSettings<ChuteSettings>().ErrorChuteNumber);
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
            package.SetStatus(PackageStatus.Error,$"{ex.Message}");
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
            chuteItem.Value = package.ChuteNumber.ToString();
            chuteItem.Description = package.ChuteNumber == 0 ? "等待分配..." : "目标分拣位置";
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
            totalItem.Value = PackageHistory.Count.ToString();
            totalItem.Description = $"累计处理 {PackageHistory.Count} 个包裹";
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
            var hourAgo = DateTime.Now.AddHours(-1);
            var hourlyCount = PackageHistory.Count(p => p.CreateTime > hourAgo);
            efficiencyItem.Value = hourlyCount.ToString();
            efficiencyItem.Description = $"最近一小时处理 {hourlyCount} 个";
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

    private void Dispose(bool disposing)
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