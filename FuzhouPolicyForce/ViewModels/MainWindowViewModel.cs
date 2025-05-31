using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BalanceSorting.Models;
using BalanceSorting.Service;
using Camera.Interface;
using Common.Models;
using Common.Models.Package;
using Common.Models.Settings.ChuteRules;
using Common.Services.Settings;
using FuzhouPolicyForce.Models;
using FuzhouPolicyForce.WangDianTong;
using Serilog;
using History.Data;

namespace FuzhouPolicyForce.ViewModels;

internal class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly ICameraService _cameraService;
    private readonly IDialogService _dialogService;
    private readonly ISettingsService _settingsService;
    private readonly IPendulumSortService _sortService;
    private readonly List<IDisposable> _subscriptions = [];

    private readonly DispatcherTimer _timer;

    private string _currentBarcode = string.Empty;

    private BitmapSource? _currentImage;

    private bool _disposed;

    private readonly IWangDianTongApiServiceV2 _wangDianTongApiServiceV2;

    private long _totalPackageCount;
    private long _errorPackageCount;

    private SystemStatus _systemStatus = new();

    private readonly IPackageHistoryDataService _packageHistoryDataService;

    public MainWindowViewModel(
        IDialogService dialogService,
        ICameraService cameraService,
        ISettingsService settingsService,
        IPendulumSortService sortService,
        IWangDianTongApiServiceV2 wangDianTongApiServiceV2,
        IPackageHistoryDataService packageHistoryDataService)
    {
        _dialogService = dialogService;
        _cameraService = cameraService;
        _settingsService = settingsService;
        _sortService = sortService;
        _wangDianTongApiServiceV2 = wangDianTongApiServiceV2;
        _packageHistoryDataService = packageHistoryDataService;

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
        _subscriptions.Add(_cameraService.PackageStream
            .ObserveOn(Scheduler.CurrentThread)
            .Subscribe(OnPackageInfo));

        // 订阅图像流
        _subscriptions.Add(_cameraService.ImageStreamWithId
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
                            CurrentImage = bitmapSource.Image;
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

        // 主动查询一次相机连接状态，防止事件先于窗口初始化触发
        OnCameraConnectionChanged(null, _cameraService.IsConnected);
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
        _dialogService.ShowDialog("HistoryDialogView");
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
            Log.Debug("已添加相机状态");

            // 获取分拣配置
            var sortConfig = _settingsService.LoadSettings<PendulumSortConfig>();
            Log.Debug("已加载分拣配置，光电数量: {Count}", sortConfig.SortingPhotoelectrics.Count);

            // 添加触发光电状态
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "触发光电",
                Status = "未连接",
                Icon = "LightbulbPerson24",
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
                    Icon = "LightbulbPerson24",
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
            icon: "CubeMultiple24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            label: "异常数",
            value: "0",
            unit: "个",
            description: "处理异常的包裹数量",
            icon: "AlertOff24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            label: "预测效率",
            value: "0",
            unit: "个/小时",
            description: "预计每小时处理量",
            icon: "ArrowTrending24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            label: "平均处理时间",
            value: "0",
            unit: "ms",
            description: "单个包裹平均处理时间",
            icon: "Timer24"
        ));
    }

    private void InitializePackageInfoItems()
    {
        PackageInfoItems.Add(new PackageInfoItem(
            label: "重量",
            value: "--",
            unit: "kg",
            description: "包裹重量",
            icon: "Scales24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            label: "尺寸",
            value: "--",
            unit: "cm",
            description: "长×宽×高",
            icon: "Ruler24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            label: "分拣口",
            value: "--",
            description: "目标分拣位置",
            icon: "ArrowCircleDown24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            label: "处理时间",
            value: "--",
            unit: "ms",
            description: "系统处理耗时",
            icon: "Timer24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            label: "当前时间",
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
        Interlocked.Increment(ref _totalPackageCount);
        try
        {
            Application.Current.Dispatcher.Invoke(() => { CurrentBarcode = package.Barcode; });

            // 获取格口规则配置 - 始终需要，用于本地规则和异常口
            var chuteSettings = _settingsService.LoadSettings<ChuteSettings>();

            // 检查条码是否为空或noread - 这是最早的判断
            if (string.IsNullOrEmpty(package.Barcode) ||
                string.Equals(package.Barcode, "noread", StringComparison.OrdinalIgnoreCase))
            {
                package.SetChute(chuteSettings.NoReadChuteNumber);
                package.SetStatus(PackageStatus.Failed, "条码为空或无法识别");
                Log.Warning("包裹条码为空或noread，使用异常口：{NoReadChute}", chuteSettings.NoReadChuteNumber);
                Interlocked.Increment(ref _errorPackageCount);

                // 直接处理包裹，跳过API调用和本地规则匹配
                _sortService.ProcessPackage(package);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        UpdatePackageInfoItems(package);
                        PackageHistory.Insert(0, package);
                        while (PackageHistory.Count > 1000) { var removedPackage = PackageHistory[^1]; PackageHistory.RemoveAt(PackageHistory.Count - 1); removedPackage.Dispose(); }
                        UpdateStatistics();
                    }
                    catch (Exception ex) { Log.Error(ex, "更新UI时发生错误"); }
                });
                await SavePackageRecordAsync(package); // 保存记录
                return; // 处理完毕，退出方法
            }

            // 1. 调用旺店通V2接口并根据结果和本地规则分配格口
            string wdtApiAttemptMessage;
            try
            {
                var wdtRequest = new WeightPushRequestV2
                {
                    LogisticsNo = package.Barcode,
                    Weight = (decimal)package.Weight,
                    Volume = package.Volume.HasValue ? (decimal?)package.Volume.Value : null,
                    Length = package.Length.HasValue ? (decimal?)package.Length.Value : null,
                    Width = package.Width.HasValue ? (decimal?)package.Width.Value : null,
                    Height = package.Height.HasValue ? (decimal?)package.Height.Value : null,
                    IsWeight = "Y"
                };
                var wdtResponse = await _wangDianTongApiServiceV2.PushWeightAsync(wdtRequest);
                
                if (wdtResponse.ServiceSuccess) // "正常" - WDT API 调用成功且服务报告成功
                {
                    wdtApiAttemptMessage = $"旺店通接口成功返回: {wdtResponse.Message ?? "无详细信息"}";
                    Log.Information("包裹 {Barcode} {WdtApiAttemptMessage}", package.Barcode, wdtApiAttemptMessage);

                    // 根据本地规则获取格口
                    var matchedChute = chuteSettings.FindMatchingChute(package.Barcode);
                    if (matchedChute.HasValue)
                    {
                        package.SetChute(matchedChute.Value);
                        package.SetStatus(PackageStatus.Success, "旺店通成功，本地规则分拣");
                        Log.Information("包裹 {Barcode} (旺店通API成功后) 本地规则匹配到格口 {Chute}", package.Barcode, matchedChute.Value);
                    }
                    else
                    {
                        package.SetChute(chuteSettings.ErrorChuteNumber);
                        package.SetStatus(PackageStatus.Failed, "旺店通成功，本地规则未匹配");
                        Log.Warning("包裹 {Barcode} (旺店通API成功后) 本地规则未匹配，使用错误口: {ErrorChute}", package.Barcode, chuteSettings.ErrorChuteNumber);
                        Interlocked.Increment(ref _errorPackageCount);
                    }
                }
                else // "异常" - WDT API 调用成功但服务报告失败
                {
                    wdtApiAttemptMessage = $"旺店通接口处理失败: {wdtResponse.Message}";
                    Log.Warning("包裹 {Barcode} {WdtApiAttemptMessage}", package.Barcode, wdtApiAttemptMessage);
                    package.SetChute(chuteSettings.ErrorChuteNumber);
                    package.SetStatus(PackageStatus.Error, "旺店通接口处理失败");
                    Interlocked.Increment(ref _errorPackageCount);
                }
            }
            catch (Exception ex) // "异常" - WDT API 调用本身失败 (例如网络问题)
            {
                wdtApiAttemptMessage = $"旺店通接口调用异常: {ex.Message}";
                Log.Error(ex, "包裹 {Barcode} {WdtApiAttemptMessage}", package.Barcode, wdtApiAttemptMessage);
                package.SetChute(chuteSettings.ErrorChuteNumber);
                package.SetStatus(PackageStatus.Error, "旺店通接口调用异常");
                Interlocked.Increment(ref _errorPackageCount);
            }

            // 分拣服务调用
            _sortService.ProcessPackage(package);

            // 2. 如果条码第一字符是7，则异步调用申通揽收接口 (即发即忘)
            if (!string.IsNullOrEmpty(package.Barcode) && package.Barcode.StartsWith('7'))
            {
                Log.Information("包裹 {Barcode}: 条码以 '7' 开头，将异步发起申通揽收接口调用。", package.Barcode);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var stConfig = _settingsService.LoadSettings<ShenTongLanShouConfig>();
                        var cangKuRequest = new CangKuAutoRequest
                        {
                            WhCode = stConfig.WhCode,
                            OrgCode = stConfig.OrgCode,
                            UserCode = stConfig.UserCode,
                            Packages =
                            [
                                new CangKuAutoPackageDto
                                {
                                    WaybillNo = package.Barcode,
                                    Weight = package.Weight.ToString("F2"), // 保留F2格式
                                    OpTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                                }
                            ]
                        };
                        var lanShouService = new Services.ShenTongLanShouService(_settingsService);
                        var stResponse = await lanShouService.UploadCangKuAutoAsync(cangKuRequest);
                        Log.Information("包裹 {Barcode}: 申通揽收接口异步调用尝试完成。响应详情: {@StResponse}", package.Barcode, stResponse);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "包裹 {Barcode}: 异步调用申通揽收接口时发生异常。", package.Barcode);
                    }
                });
            }
            else
            {
                if (string.IsNullOrEmpty(package.Barcode)) {
                     Log.Debug("包裹条码为空，跳过申通揽收接口调用检查。");
                } else {
                     Log.Debug("包裹 {Barcode}: 条码不以 '7' 开头 (首字符: {FirstChar})，跳过申通揽收接口调用。", package.Barcode, package.Barcode[0]);
                }
            }
            
            // UI更新和数据保存
            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    UpdatePackageInfoItems(package);
                    PackageHistory.Insert(0, package);
                    while (PackageHistory.Count > 1000)
                    {
                        var removedPackage = PackageHistory[^1];
                        PackageHistory.RemoveAt(PackageHistory.Count - 1);
                        removedPackage.Dispose();
                    }
                    UpdateStatistics();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "更新UI时发生错误");
                }
            });

            await SavePackageRecordAsync(package);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "处理包裹 {Barcode} 时发生未处理的异常", package.Barcode);
            package.SetStatus(PackageStatus.Error, $"未处理异常: {ex.Message}");
            try
            {
                // 尝试在顶层异常中设置错误口
                var chuteSettings = _settingsService.LoadSettings<ChuteSettings>();
                package.SetChute(chuteSettings.ErrorChuteNumber);
            }
            catch (Exception chuteEx) 
            {
                Log.Error(chuteEx, "在顶层异常处理中设置错误口失败");
            }
            Interlocked.Increment(ref _errorPackageCount);
            
            // 尝试保存记录和更新UI，即使发生顶层异常
            try
            {
                await SavePackageRecordAsync(package);
            }
            catch(Exception saveEx)
            {
                Log.Error(saveEx, "顶层异常处理后保存包裹记录失败: {Barcode}", package.Barcode);
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    UpdatePackageInfoItems(package);
                    PackageHistory.Insert(0, package);
                    while (PackageHistory.Count > 1000) { var removedPackage = PackageHistory[^1]; PackageHistory.RemoveAt(PackageHistory.Count - 1); removedPackage.Dispose(); }
                    UpdateStatistics();
                }
                catch (Exception uiEx) { Log.Error(uiEx, "顶层异常处理后更新UI时发生错误"); }
            });
        }
        finally
        {
            package.ReleaseImage(); // 确保图像资源总是被释放
        }
    }

    // Helper method to save package record, extracted to reduce duplication
    private async Task SavePackageRecordAsync(PackageInfo package)
    {
         try
         {
             var record = PackageHistoryRecord.FromPackageInfo(package);
             await _packageHistoryDataService.AddPackageAsync(record);
             Log.Information("包裹记录已保存到数据库：{Barcode}", package.Barcode);
         }
         catch (Exception ex)
         {
             Log.Error(ex, "保存包裹记录到数据库时发生错误：{Barcode}", package.Barcode);
             // 数据库保存失败是否算作异常？此处不重复增加_errorPackageCount，因为它反映的是业务处理异常。
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
            weightItem.Value = package.Weight.ToString("F3", CultureInfo.InvariantCulture);
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
             chuteItem.StatusColor = package.Status == PackageStatus.Success ? "#4CAF50" : "#F44336";
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
            // 使用新的总数计数器
            totalItem.Value = _totalPackageCount.ToString();
            totalItem.Description = $"累计处理 {_totalPackageCount} 个包裹";
        }

        var errorItem = StatisticsItems.FirstOrDefault(static x => x.Label == "异常数");
        if (errorItem != null)
        {
            // 使用新的异常数计数器
            // var errorCount = PackageHistory.Count(static p => !string.IsNullOrEmpty(p.ErrorMessage)); // 移除旧逻辑
            var errorCount = _errorPackageCount; // 使用新计数器·
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