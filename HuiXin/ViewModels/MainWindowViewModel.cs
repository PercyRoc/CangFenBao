using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Common.Data;
using Common.Models.Package;
using Common.Models.Settings.ChuteRules;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Services;
using Serilog;
using SharedUI.Models;
using SortingServices.Car.Service;
using SortingServices.Servers.Services.JuShuiTan;

namespace HuiXin.ViewModels;

public class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly ICameraService _cameraService;
    private readonly CarSortService _carSortService;
    private readonly IDialogService _dialogService;
    private readonly IJuShuiTanService _juShuiTanService;
    private readonly IPackageDataService _packageDataService;
    private readonly ISettingsService _settingsService;
    private readonly List<IDisposable> _subscriptions = [];

    private readonly DispatcherTimer _timer;

    private string _currentBarcode = string.Empty;

    private BitmapSource? _currentImage;

    private int _currentPackageIndex;

    private bool _disposed;

    private SystemStatus _systemStatus = new();

    public MainWindowViewModel(
        IDialogService dialogService,
        ICameraService cameraService,
        ISettingsService settingsService,
        PackageTransferService packageTransferService,
        IPackageDataService packageDataService,
        IJuShuiTanService juShuiTanService,
        CarSortService carSortService)
    {
        _dialogService = dialogService;
        _cameraService = cameraService;
        _settingsService = settingsService;
        _packageDataService = packageDataService;
        _juShuiTanService = juShuiTanService;
        _carSortService = carSortService;

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

        // 订阅相机连接状态事件
        _cameraService.ConnectionChanged += OnCameraConnectionChanged;

        // 初始化小车分拣服务
        InitializeCarSortServiceAsync();

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
    }

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

        // 更新小车连接状态
        UpdateCarStatus(_carSortService.IsConnected, _carSortService.IsRunning);
    }

    private async void InitializeCarSortServiceAsync()
    {
        try
        {
            // 初始化小车分拣服务
            var initialized = await _carSortService.InitializeAsync();
            if (initialized)
            {
                // 启动服务
                var started = await _carSortService.StartAsync();
                Log.Information("小车分拣服务初始化状态: {Initialized}, 启动状态: {Started}",
                    initialized, started);
                UpdateCarStatus(_carSortService.IsConnected, started);
            }
            else
            {
                Log.Error("小车分拣服务初始化失败");
                UpdateCarStatus(false, false);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化小车分拣服务时发生异常");
            UpdateCarStatus(false, false);
        }
    }

    private void InitializeDeviceStatuses()
    {
        try
        {
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "相机",
                Status = "未连接",
                Icon = "Camera24",
                StatusColor = "#F44336" // 红色表示未连接
            });

            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "小车",
                Status = "未连接",
                Icon = "Vehicle24",
                StatusColor = "#F44336" // 红色表示未连接
            });
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
            "异常数",
            "0",
            "个",
            "处理异常的包裹数量",
            "ErrorCircle24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            "预测效率",
            "0",
            "个/小时",
            "预计每小时处理量",
            "ArrowTrendingLines24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            "平均处理时间",
            "0",
            "ms",
            "单个包裹平均处理时间",
            "Timer24"
        ));
    }

    private void InitializePackageInfoItems()
    {
        PackageInfoItems.Add(new PackageInfoItem(
            "条码",
            "--",
            description: "包裹条码信息",
            icon: "BarcodeScanner24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "重量",
            "--",
            "kg",
            "包裹重量",
            "Scales24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "尺寸",
            "--",
            "cm",
            "长×宽×高",
            "Ruler24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "格口",
            "--",
            description: "目标分拣位置",
            icon: "ArrowCircleDown24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "处理时间",
            "--",
            "ms",
            "系统处理耗时",
            "Timer24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "时间",
            "--:--:--",
            description: "包裹处理时间",
            icon: "Clock24"
        ));
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
            Application.Current.Dispatcher.Invoke(() => { CurrentBarcode = package.Barcode; });

            // 获取格口规则配置
            var chuteSettings = _settingsService.LoadSettings<ChuteSettings>();

            // 判断条码是否为空或noread
            var isNoRead = string.IsNullOrEmpty(package.Barcode) ||
                           string.Equals(package.Barcode, "noread", StringComparison.OrdinalIgnoreCase);

            if (isNoRead)
            {
                // 条码无法识别，使用异常口
                package.SetChute(chuteSettings.ErrorChuteNumber);
                package.SetStatus(PackageStatus.Failed, "条码为空或无法识别");
                Log.Warning("包裹条码为空或noread，使用异常口：{ErrorChute}", chuteSettings.ErrorChuteNumber);
            }
            else
            {
                try
                {
                    // 上传到聚水潭
                    var weightSendRequest = new WeightSendRequest
                    {
                        LogisticsId = package.Barcode,
                        Weight = (decimal)package.Weight,
                        Type = 5
                    };

                    Log.Information("上传包裹 {Barcode} 到聚水潭", package.Barcode);
                    var response = await _juShuiTanService.WeightAndSendAsync(weightSendRequest);

                    if (response.Code == 0)
                    {
                        Log.Information("聚水潭上传成功: {Barcode}", package.Barcode);

                        // 尝试匹配格口规则
                        var matchedChute = chuteSettings.FindMatchingChute(package.Barcode, package.Weight);

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
                    else
                    {
                        // 聚水潭响应异常，使用异常口
                        package.SetChute(chuteSettings.ErrorChuteNumber);
                        package.SetStatus(PackageStatus.Failed, $"聚水潭错误: {response.Message}");
                        Log.Error("聚水潭上传失败: {Code}, {Message}, 使用异常口: {ErrorChute}",
                            response.Code, response.Message, chuteSettings.ErrorChuteNumber);
                    }
                }
                catch (Exception ex)
                {
                    // 处理异常，使用异常口
                    package.SetChute(chuteSettings.ErrorChuteNumber);
                    package.SetStatus(PackageStatus.Error, $"聚水潭异常: {ex.Message}");
                    Log.Error(ex, "上传到聚水潭时发生错误: {Barcode}", package.Barcode);
                }
            }

            // 如果包裹状态不是错误，设为成功
            if (package.Status != PackageStatus.Error && package.Status != PackageStatus.Failed)
            {
                package.SetStatus(PackageStatus.Success);
            }

            // 更新UI
            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    UpdatePackageInfoItems(package);
                    // 更新统计信息和历史包裹列表
                    PackageHistory.Insert(0, package);
                    while (PackageHistory.Count > 1000) // 保持最近1000条记录
                    {
                        var removedPackage = PackageHistory[^1];
                        PackageHistory.RemoveAt(PackageHistory.Count - 1);
                        removedPackage.Dispose(); // 释放被移除的包裹
                    }

                    // 递增包裹计数器
                    Interlocked.Increment(ref _currentPackageIndex);
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

            // 添加到小车分拣队列
            if (package.ChuteNumber <= 0) return;
            {
                try
                {
                    // 添加到分拣队列
                    var added = await _carSortService.ProcessPackageSortingAsync(package);

                    if (added)
                    {
                        Log.Information("包裹 {Barcode} 已成功添加到分拣队列", package.Barcode);
                    }
                    else
                    {
                        Log.Error("包裹 {Barcode} 添加到分拣队列失败", package.Barcode);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "添加包裹到分拣队列时发生异常: {Barcode}", package.Barcode);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹信息时发生错误：{Barcode}", package.Barcode);
            package.SetStatus(PackageStatus.Error, $"{ex.Message}");
        }
        finally
        {
            package.ReleaseImage(); // 确保包裹被释放
        }
    }

    private void UpdateCarStatus(bool isConnected, bool isRunning)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var carStatus = DeviceStatuses.FirstOrDefault(s => s.Name == "小车");
            if (carStatus == null) return;

            if (!isConnected)
            {
                carStatus.Status = "未连接";
                carStatus.StatusColor = "#F44336"; // 红色
            }
            else if (!isRunning)
            {
                carStatus.Status = "已连接(停止)";
                carStatus.StatusColor = "#FFC107"; // 黄色
            }
            else
            {
                carStatus.Status = "运行中";
                carStatus.StatusColor = "#4CAF50"; // 绿色
            }
        });
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