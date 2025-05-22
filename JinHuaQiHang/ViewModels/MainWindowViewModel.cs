using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Threading;
using BalanceSorting.Models;
using BalanceSorting.Service;
using Camera.Services.Implementations.TCP;
using Common.Models.Package;
using Common.Services.Audio;
using Common.Services.Settings;
using Common.Services.Ui;
using Serilog;
using SharedUI.Models;
using Server.JuShuiTan.Services;
using History.Data;

namespace JinHuaQiHang.ViewModels;

public class MainWindowViewModel : BindableBase,IDisposable
{
    private readonly TcpCameraService _cameraService;
    private readonly IPendulumSortService _sortService;
    private readonly IDialogService _dialogService;
    private readonly ISettingsService _settingsService;
    private readonly INotificationService _notificationService;
    private readonly IAudioService _audioService;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly DispatcherTimer _timer;
    private string _currentBarcode = string.Empty;
    private bool _disposed;
    private SystemStatus _systemStatus = new();
    private DateTime? _firstPackageTime;
    private readonly IJuShuiTanService _juShuiTanService;
    private readonly IPackageHistoryDataService _packageHistoryDataService;

public MainWindowViewModel(
        IDialogService dialogService,
        TcpCameraService cameraService,
        ISettingsService settingsService,
        IPendulumSortService sortService,
        INotificationService notificationService,
        IAudioService audioService,
        IJuShuiTanService juShuiTanService,
        IPackageHistoryDataService packageHistoryDataService)
    {
        _dialogService = dialogService;
        _cameraService = cameraService;
        _settingsService = settingsService;
        _sortService = sortService;
        _notificationService = notificationService;
        _audioService = audioService;
        _juShuiTanService = juShuiTanService;
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

        // 订阅包裹流 - 移除 Buffer 和 Select
        _subscriptions.Add(_cameraService.PackageStream
            .ObserveOn(Scheduler.CurrentThread) // 切换回UI线程
            .Subscribe(OnPackageInfo));
    }

    public DelegateCommand OpenSettingsCommand { get; }
    public DelegateCommand OpenHistoryCommand { get; }
    
    public string CurrentBarcode
    {
        get => _currentBarcode;
        set => SetProperty(ref _currentBarcode, value);
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
    
    private void ExecuteOpenSettings()
    {
        _dialogService.ShowDialog("SettingsDialogs", new DialogParameters(), _ => { });

    }

    private void ExecuteOpenHistory()
    {
        _dialogService.ShowDialog("PackageHistoryDialogView", new DialogParameters(), _ => { });
    }
    
    private void Timer_Tick(object? sender, EventArgs e)
    {
        try
        {
            SystemStatus = SystemStatus.GetCurrentStatus();
            Application.Current?.Dispatcher.InvokeAsync(UpdateStatistics);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "无法更新系统状态或统计信息。");
        }
    }
    
    private void InitializeDeviceStatuses()
    {
        try
        {
            DeviceStatuses.Clear();
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "相机",
                Status = "未连接",
                Icon = "Camera24",
                StatusColor = "#F44336"
            });
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
            
        }
        catch (Exception ex)
        {
            Log.Error(ex, "错误初始化设备状态列表。");
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
        try
        {
            // 1. 实时展示条码和卡片信息
            Application.Current.Dispatcher.Invoke(() =>
            {
                CurrentBarcode = package.Barcode;
                UpdatePackageInfoItems(package);
            });

            // 2. 上传到聚水潭系统（先上传）
            var request = new SortingServices.Servers.Services.JuShuiTan.WeightSendRequest
            {
                LogisticsId = package.Barcode,
                Weight = (decimal)package.Weight,
                IsUnLid = false,
                Type = 5,
                Volume = package.Volume.HasValue ? (decimal?)package.Volume.Value / 1000000m : null, // 立方厘米转立方米
                Channel = "自动分拣"
            };
            var response = await _juShuiTanService.WeightAndSendAsync(request);
            var chuteSettings = _settingsService.LoadSettings<Common.Models.Settings.ChuteRules.ChuteSettings>();
            if (response.Code != 0)
            {
                // 上传失败，分配到异常格口
                package.SetChute(chuteSettings.ErrorChuteNumber > 0 ? chuteSettings.ErrorChuteNumber : 1);
                package.SetStatus(PackageStatus.Error, $"聚水潭失败:{response.Message}");
                package.ErrorMessage = response.Message;
            }
            else
            {
                // 上传成功，按规则分配格口
                int? matchedChute = null;
                if (!string.IsNullOrWhiteSpace(package.Barcode))
                {
                    matchedChute = chuteSettings.FindMatchingChute(package.Barcode);
                }
                if (matchedChute.HasValue)
                {
                    package.SetChute(matchedChute.Value);
                }
                else if (string.IsNullOrWhiteSpace(package.Barcode) && chuteSettings.NoReadChuteNumber > 0)
                {
                    package.SetChute(chuteSettings.NoReadChuteNumber);
                }
                else if (chuteSettings.ErrorChuteNumber > 0)
                {
                    package.SetChute(chuteSettings.ErrorChuteNumber);
                }
                else
                {
                    package.SetChute(1); // 默认分配到1号口，防御性兜底
                }
                package.SetStatus(package.Status, "上传聚水潭成功");
            }

            // 3. 调用摆轮分拣
            _sortService.ProcessPackage(package);

            // 4. 添加历史记录、统计信息、写入数据库
            Application.Current.Dispatcher.Invoke(() =>
            {
                PackageHistory.Insert(0, package);
                if (PackageHistory.Count > 1000) PackageHistory.RemoveAt(PackageHistory.Count - 1); // 限制历史条数
                UpdateStatistics();
            });
            var record = PackageHistoryRecord.FromPackageInfo(package);
            await _packageHistoryDataService.AddPackageAsync(record);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹信息时发生异常");
            _notificationService.ShowError($"包裹处理异常: {ex.Message}");
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
        var totalPackagesItem = StatisticsItems.FirstOrDefault(static i => i.Label == "总包裹数");
        var errorCountItem = StatisticsItems.FirstOrDefault(static i => i.Label == "异常数");
        var efficiencyItem = StatisticsItems.FirstOrDefault(static i => i.Label == "预测效率");
        var avgProcessingTimeItem = StatisticsItems.FirstOrDefault(static i => i.Label == "平均处理时间");

        if (totalPackagesItem == null || errorCountItem == null || efficiencyItem == null ||
            avgProcessingTimeItem == null)
        {
            Log.Warning("一个或多个统计项未找到，无法更新统计信息。");
            return;
        }

        var history = PackageHistory.ToList(); // 创建副本以进行线程安全的迭代
        var totalCount = history.Count;

        // 更新总包裹数
        totalPackagesItem.Value = totalCount.ToString();

        // 更新异常数
        var errorCount = history.Count(p => p.Status == PackageStatus.Error);
        errorCountItem.Value = errorCount.ToString();

        // 更新平均处理时间
        if (totalCount > 0)
        {
            var avgProcessingTime = history.Average(p => p.ProcessingTime);
            avgProcessingTimeItem.Value = avgProcessingTime.ToString("F0"); // 保留0位小数
        }
        else
        {
            avgProcessingTimeItem.Value = "0";
        }

        // 更新预测效率
        if (_firstPackageTime == null && totalCount > 0)
        {
            // 如果是第一个包裹，记录时间
            _firstPackageTime = DateTime.Now;
        }

        if (_firstPackageTime.HasValue && totalCount > 0)
        {
            var elapsedTime = DateTime.Now - _firstPackageTime.Value;
            // 防止过短时间导致除零或效率过高
            if (elapsedTime.TotalSeconds >= 1)
            {
                var efficiency = totalCount / elapsedTime.TotalHours;
                efficiencyItem.Value = efficiency.ToString("F0"); // 保留0位小数
            }
            else
            {
                // 时间太短，暂时显示为0或其他合适的值
                efficiencyItem.Value = "0";
            }
        }
        else
        {
            efficiencyItem.Value = "0";
        }
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _subscriptions.ForEach(s => s.Dispose());
            _timer.Stop();
        }

        // Dispose unmanaged resources
        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}