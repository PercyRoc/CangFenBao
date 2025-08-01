using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Common.Data;
using Common.Models.Package;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.Rfid;
using FridFlipMachine.Events;
using FridFlipMachine.Models;
using FridFlipMachine.Services;
using Serilog;
using SharedUI.Models;

namespace FridFlipMachine.ViewModels;

public class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly IFridService _fridService;
    private readonly IDialogService _dialogService;
    private readonly IEventAggregator _eventAggregator;
    private readonly IPackageDataService _packageDataService;
    private readonly SortingService _sortingService;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly ITcpConnectionService _tcpConnectionService;
    private readonly DispatcherTimer _timer;
    private string _currentBarcode = string.Empty;
    private BitmapSource? _currentImage;
    private bool _disposed;
    private int _historyIndexCounter;
    private PlateTurnoverSettings _settings;
    private SystemStatus _systemStatus = new();

    public MainWindowViewModel(
        IDialogService dialogService,
        IFridService fridService,
        FridPackageService fridPackageService,
        SortingService sortingService,
        ITcpConnectionService tcpConnectionService,
        ISettingsService settingsService,
        IEventAggregator eventAggregator,
        IPackageDataService packageDataService)
    {
        _dialogService = dialogService;
        _fridService = fridService;
        _sortingService = sortingService;
        _packageDataService = packageDataService;
        _tcpConnectionService = tcpConnectionService;
        _eventAggregator = eventAggregator;

        // 加载初始配置
        _settings = settingsService.LoadSettings<PlateTurnoverSettings>();

        // 初始化命令
        OpenSettingsCommand = new DelegateCommand(ExecuteOpenSettings);
        ShowHistoryCommand = new DelegateCommand(ExecuteShowHistory);

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

        // 订阅Frid连接状态事件
        _fridService.ConnectionChanged += OnFridConnectionChanged;

        // 订阅触发光电连接状态事件
        _tcpConnectionService.TriggerPhotoelectricConnectionChanged += OnTriggerPhotoelectricConnectionChanged;

        // 订阅TCP模块连接状态变化
        _tcpConnectionService.TcpModuleConnectionChanged += OnTcpModuleConnectionChanged;

        // 订阅配置更新事件
        _eventAggregator.GetEvent<PlateTurnoverSettingsUpdatedEvent>().Subscribe(OnSettingsUpdated);

        // 订阅包裹流
        _subscriptions.Add(fridPackageService.PackageStream
            .ObserveOn(Scheduler.CurrentThread)
            .Subscribe(OnPackageInfo));
    }

    #region Properties

    public DelegateCommand OpenSettingsCommand { get; }
    public DelegateCommand ShowHistoryCommand { get; }

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

    #endregion

    #region Private Methods

    private void ExecuteOpenSettings()
    {
        _dialogService.ShowDialog("SettingsDialog");
    }

    private void ExecuteShowHistory()
    {
        try
        {
            _dialogService.ShowDialog("HistoryDialog");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "显示历史记录对话框时发生错误");
        }
    }

    private void OnSettingsUpdated(PlateTurnoverSettings newSettings)
    {
        Log.Information("主窗口接收到实时配置更新。");
        _settings = newSettings;
        // 配置更新后，立即更新TCP模块的状态显示，以反映可能变化的总数
        Application.Current.Dispatcher.Invoke(UpdateTcpModuleStatus);
        Log.Information("主窗口已应用翻板机实时配置。");
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        SystemStatus = SystemStatus.GetCurrentStatus();
    }

    private void OnFridConnectionChanged(bool isConnected)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var fridStatus = DeviceStatuses.FirstOrDefault(static s => s.Name == "Frid");
            if (fridStatus == null) return;

            fridStatus.Status = isConnected ? "已连接" : "已断开";
            fridStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
        });
    }

    private void OnTriggerPhotoelectricConnectionChanged(object? sender, bool isConnected)
    {
        try
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var status = DeviceStatuses.FirstOrDefault(static s => s.Name == "触发光电");
                if (status == null) return;

                status.Status = isConnected ? "已连接" : "已断开";
                status.StatusColor = isConnected ? "#4CAF50" : "#F44336";
                Log.Information("触发光电连接状态更新为：{Status}", status.Status);
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新触发光电状态时发生错误");
        }
    }

    private void OnTcpModuleConnectionChanged(object? sender, ValueTuple<TcpConnectionConfig, bool> e)
    {
        try
        {
            // 确保在UI线程上更新状态
            Application.Current.Dispatcher.Invoke(() =>
            {
                UpdateTcpModuleStatus();
                Log.Information("TCP模块 {IpAddress} 连接状态更新为：{Status}",
                    e.Item1.IpAddress, e.Item2 ? "已连接" : "已断开");
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理TCP模块连接状态变化时发生错误");
        }
    }

    private void OnPackageInfo(PackageInfo package)
    {
        try
        {
            // 处理 noread 包裹，直接跳过
            if (package.Barcode.Equals("noread", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // 将包裹加入队列以进行物理分拣 (无论API结果如何，都入队处理)
            _sortingService.EnqueuePackage(package);

            // 保存包裹到数据库（异步执行，不阻塞分拣流程）
            _ = Task.Run(async () =>
            {
                try
                {
                    await _packageDataService.AddPackageAsync(package);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "保存包裹 {Barcode} 到数据库时发生错误", package.Barcode);
                }
            });

            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    package.Index = Interlocked.Increment(ref _historyIndexCounter);
                    // 更新当前条码
                    CurrentBarcode = package.Barcode;
                    // 更新实时包裹数据
                    UpdatePackageInfoItems(package);

                    // 更新历史包裹列表
                    UpdatePackageHistory(package);
                    // 更新统计信息
                    UpdateStatistics(package);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "更新UI时发生错误");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹信息时发生意外错误：{Barcode}", package.Barcode);
            package.SetStatus(PackageStatus.Error, "处理过程中发生意外错误。");
        }
    }

    private void UpdatePackageInfoItems(PackageInfo package)
    {
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

        var chuteItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "格口");
        if (chuteItem != null)
        {
            // 优先显示完整格口代码，否则显示数字格口号
            var chuteDisplay = !string.IsNullOrEmpty(package.SortPortCode) ? package.SortPortCode :
                package.ChuteNumber > 0 ? package.ChuteNumber.ToString() : "--";
            chuteItem.Value = chuteDisplay;
            chuteItem.Description = package.ChuteNumber > 0 ? "目标格口" : "等待分配...";
        }

        var timeItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "时间");
        if (timeItem != null)
        {
            timeItem.Value = package.CreateTime.ToString("HH:mm:ss");
            timeItem.Description = $"处理于 {package.CreateTime:yyyy-MM-dd}";
        }

        var statusItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "状态");
        if (statusItem == null) return;

        statusItem.Value = string.IsNullOrEmpty(package.ErrorMessage) ? "正常" : "异常";
        statusItem.Description = string.IsNullOrEmpty(package.ErrorMessage) ? "处理成功" : package.ErrorMessage;
    }

    private void UpdatePackageHistory(PackageInfo package)
    {
        try
        {
            // 限制历史记录数量，保持最新的1000条记录
            const int maxHistoryCount = 1000;

            // 添加到历史记录开头
            PackageHistory.Insert(0, package);

            // 如果超出最大数量，移除多余的记录
            while (PackageHistory.Count > maxHistoryCount)
                PackageHistory.RemoveAt(PackageHistory.Count - 1);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新历史包裹列表时发生错误");
        }
    }

    private void UpdateStatistics(PackageInfo package)
    {
        try
        {
            // 更新总包裹数
            var totalItem = StatisticsItems.FirstOrDefault(static x => x.Label == "总包裹数");
            if (totalItem != null)
            {
                var total = int.Parse(totalItem.Value) + 1;
                totalItem.Value = total.ToString();
            }

            // 更新成功/失败数
            var isSuccess = string.IsNullOrEmpty(package.ErrorMessage);
            var targetLabel = isSuccess ? "成功数" : "失败数";
            var statusItem = StatisticsItems.FirstOrDefault(x => x.Label == targetLabel);
            if (statusItem != null)
            {
                var count = int.Parse(statusItem.Value) + 1;
                statusItem.Value = count.ToString();
            }

            // 更新处理速率（每小时包裹数）
            var speedItem = StatisticsItems.FirstOrDefault(static x => x.Label == "处理速率");
            if (speedItem == null || PackageHistory.Count < 2) return;
            // 获取最早和最新的包裹时间差
            var latestTime = PackageHistory[0].CreateTime;
            var earliestTime = PackageHistory[^1].CreateTime;
            var timeSpan = latestTime - earliestTime;

            if (!(timeSpan.TotalSeconds > 0)) return;
            // 计算每小时处理数量
            var hourlyRate = PackageHistory.Count / timeSpan.TotalHours;
            speedItem.Value = Math.Round(hourlyRate).ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新统计信息时发生错误");
        }
    }

    private void InitializeDeviceStatuses()
    {
        try
        {
            Log.Debug("开始初始化设备状态列表");

            // 添加Frid状态
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "Frid",
                Status = "未连接",
                Icon = "DeviceEq24",
                StatusColor = "#F44336" // 红色表示未连接
            });

            // 添加触发光电状态
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "触发光电",
                Status = "未连接",
                Icon = "Alert24",
                StatusColor = "#F44336"
            });

            // 添加TCP模块汇总状态 - 从配置初始化
            var connectedModules = _tcpConnectionService.TcpModuleClients.Count(static x => x.Value.Connected);
            var totalConfiguredModules = _settings.Items
                .Where(item => !string.IsNullOrEmpty(item.TcpAddress))
                .Select(item => item.TcpAddress)
                .Distinct()
                .Count();
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "TCP模块",
                Status = $"{connectedModules}/{totalConfiguredModules}",
                Icon = "DeviceEq24",
                StatusColor = totalConfiguredModules > 0 && connectedModules == totalConfiguredModules ? "#4CAF50" :
                    connectedModules > 0 ? "#FFA500" : "#F44336"
            });

            Log.Information("设备状态列表初始化完成，共 {Count} 个设备", DeviceStatuses.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化设备状态列表时发生错误");
        }
    }

    private void UpdateTcpModuleStatus()
    {
        try
        {
            var status = DeviceStatuses.FirstOrDefault(static s => s.Name == "TCP模块");
            if (status == null) return;

            // 总数应始终来自配置中不重复的TCP地址数量
            var totalModules = _settings.Items
                .Where(item => !string.IsNullOrEmpty(item.TcpAddress))
                .Select(item => item.TcpAddress)
                .Distinct()
                .Count();
            var connectedModules = _tcpConnectionService.TcpModuleClients.Count(static x => x.Value.Connected);

            status.Status = $"{connectedModules}/{totalModules}";
            status.StatusColor = totalModules > 0 && connectedModules == totalModules ? "#4CAF50" :
                connectedModules > 0 ? "#FFA500" : "#F44336";

            Log.Debug("TCP模块状态更新为：{Status}, 已连接：{Connected}, 总数(配置)：{Total}",
                status.Status, connectedModules, totalModules);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新TCP模块状态时发生错误");
        }
    }

    private void InitializeStatisticsItems()
    {
        // Use constructor arguments
        StatisticsItems.Add(new StatisticsItem(
            "总包裹数", // label
            "0", // value
            "个", // unit
            "累计处理包裹总数", // description
            "BoxMultiple24" // icon
        ));

        StatisticsItems.Add(new StatisticsItem(
            "成功数", // label
            "0", // value
            "个", // unit
            "处理成功的包裹数量", // description
            "CheckmarkCircle24" // icon
        ));

        StatisticsItems.Add(new StatisticsItem(
            "失败数", // label
            "0", // value
            "个", // unit
            "处理失败的包裹数量", // description
            "ErrorCircle24" // icon
        ));

        StatisticsItems.Add(new StatisticsItem(
            "处理速率", // label
            "0", // value
            "个/小时", // unit
            "每小时处理包裹数量", // description
            "ArrowTrendingLines24" // icon
        ));
    }

    private void InitializePackageInfoItems()
    {
        // Use constructor arguments
        PackageInfoItems.Add(new PackageInfoItem(
            "重量", // label
            "0.00", // value
            "kg", // unit
            "包裹重量", // description
            "Scales24" // icon
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "尺寸", // label
            "0 × 0 × 0", // value
            "mm", // unit
            "长 × 宽 × 高", // description
            "Ruler24" // icon
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "格口", // label
            "--", // value
            "", // unit
            "目标格口", // description
            "BoxMultiple24" // icon
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "时间", // label
            "--:--:--", // value
            "", // unit
            "处理时间", // description
            "Timer24" // icon
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "状态", // label
            "等待", // value
            "", // unit
            "处理状态", // description
            "Alert24" // icon
        ));
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
            try
            {
                // 取消订阅事件
                _fridService.ConnectionChanged -= OnFridConnectionChanged;
                _tcpConnectionService.TriggerPhotoelectricConnectionChanged -= OnTriggerPhotoelectricConnectionChanged;
                _tcpConnectionService.TcpModuleConnectionChanged -= OnTcpModuleConnectionChanged;
                _eventAggregator.GetEvent<PlateTurnoverSettingsUpdatedEvent>().Unsubscribe(OnSettingsUpdated);

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

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    #endregion
}