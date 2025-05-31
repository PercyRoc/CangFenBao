using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Threading;
using BalanceSorting.Models;
using BalanceSorting.Service;
using Camera.Services.Implementations.TCP;
using Common.Models;
using Common.Models.Package;
using Common.Services.Audio;
using Common.Services.Settings;
using Common.Services.Ui;
using Serilog;
using Server.JuShuiTan.Services;
using History.Data;
using Server.JuShuiTan.Models;
using Common.Models.Settings.ChuteRules;

namespace JinHuaQiHang.ViewModels;

public class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly TcpCameraService _cameraService;
    private readonly IPendulumSortService _sortService;
    private readonly IDialogService _dialogService;
    private readonly ISettingsService _settingsService;
    private readonly INotificationService _notificationService;
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
        try
        {
            _dialogService = dialogService;
            _cameraService = cameraService;
            _settingsService = settingsService;
            _sortService = sortService;
            _notificationService = notificationService;
            _juShuiTanService = juShuiTanService;
            _packageHistoryDataService = packageHistoryDataService;

            OpenSettingsCommand = new DelegateCommand(ExecuteOpenSettings);
            OpenHistoryCommand = new DelegateCommand(ExecuteOpenHistory);

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += Timer_Tick;
            _timer.Start();

            InitializeDeviceStatuses();

            // 主动查询所有设备的初始连接状态
            var initialStates = _sortService.GetAllDeviceConnectionStates();
            foreach (var state in initialStates)
            {
                // 在UI线程更新设备状态
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var deviceStatus = DeviceStatuses.FirstOrDefault(x => x.Name == state.Key);
                    if (deviceStatus != null)
                    {
                        deviceStatus.Status = state.Value ? "已连接" : "已断开";
                        deviceStatus.StatusColor = state.Value ? "#4CAF50" : "#F44336";
                        Log.Debug("ViewModel初始化：设备状态已更新: {Name} -> {Status}", state.Key, deviceStatus.Status);
                    }
                    else
                    {
                        Log.Warning("ViewModel初始化：未找到设备状态项以更新: {Name}", state.Key);
                    }
                });
            }

            InitializeStatisticsItems();
            InitializePackageInfoItems();

            _sortService.DeviceConnectionStatusChanged += OnDeviceConnectionStatusChanged;
            _cameraService.ConnectionChanged += OnCameraConnectionChanged;

            var initialCameraId = $"TcpCamera-{_cameraService.GetHashCode()}";
            OnCameraConnectionChanged(initialCameraId, _cameraService.IsConnected);

            _subscriptions.Add(_cameraService.PackageStream
                .ObserveOn(Scheduler.CurrentThread) 
                .Subscribe(OnPackageInfo));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MainWindowViewModel] Constructor 执行期间发生严重错误.");
            if (ex.Message.Contains("ISettingsService"))
            {
                Log.Fatal(ex, "[MainWindowViewModel] ISettingsService 相关错误导致构造失败!");
            }
            throw;
        }
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
            
            var sortConfig = _settingsService.LoadSettings<PendulumSortConfig>();
            Log.Information("[MainWindowViewModel] PendulumSortConfig 已加载. 光电数量: {Count}", sortConfig.SortingPhotoelectrics.Count);

            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "触发光电",
                Status = "未连接",
                Icon = "LightbulbPerson24",
                StatusColor = "#F44336"
            });

            foreach (var photoelectric in sortConfig.SortingPhotoelectrics)
            {
                DeviceStatuses.Add(new DeviceStatus
                {
                    Name = photoelectric.Name,
                    Status = "未连接",
                    Icon = "LightbulbPerson24",
                    StatusColor = "#F44336"
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MainWindowViewModel] InitializeDeviceStatuses 期间发生错误.");
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
            deviceStatus.StatusColor = e.Connected ? "#4CAF50" : "#F44336";
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
            Application.Current.Dispatcher.Invoke(() =>
            {
                CurrentBarcode = package.Barcode;
                
            });

            var request = new WeightSendRequest
            {
                LId = package.Barcode,
                Weight = package.Weight,
                IsUnLid = false,
                Type = 5,
                FVolume = package.Volume.HasValue ? (package.Volume.Value / 1000000.0) : null,
                Channel = "自动分拣"
            };
            var response = await _juShuiTanService.WeightAndSendAsync(request);
            
            var chuteSettings = _settingsService.LoadSettings<ChuteSettings>();

            if (response.Code != 0)
            {
                package.SetChute(chuteSettings.ErrorChuteNumber > 0 ? chuteSettings.ErrorChuteNumber : 1);
                if (response.Datas != null && response.Datas.Count != 0)
                {
                    package.SetStatus($"聚水潭上传失败: {response.Datas.FirstOrDefault()?.Msg}");
                    package.ErrorMessage = response.Datas.FirstOrDefault()?.Msg;
                }
                else
                {
                    package.SetStatus("聚水潭上传失败");
                    package.ErrorMessage = "聚水潭上传失败";
                }
            }
            else
            {
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
                    package.SetChute(1);
                }
                package.SetStatus("上传聚水潭成功");
            }

            _sortService.ProcessPackage(package);

            Application.Current.Dispatcher.Invoke(() =>
            {
                UpdatePackageInfoItems(package);
                PackageHistory.Insert(0, package);
                if (PackageHistory.Count > 1000) PackageHistory.RemoveAt(PackageHistory.Count - 1);
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

        var history = PackageHistory.ToList();
        var totalCount = history.Count;

        totalPackagesItem.Value = totalCount.ToString();

        var errorCount = history.Count(p => !string.IsNullOrEmpty(p.ErrorMessage));
        errorCountItem.Value = errorCount.ToString();

        if (totalCount > 0)
        {
            var avgProcessingTime = history.Average(p => p.ProcessingTime);
            avgProcessingTimeItem.Value = avgProcessingTime.ToString("F0");
        }
        else
        {
            avgProcessingTimeItem.Value = "0";
        }

        if (_firstPackageTime == null && totalCount > 0)
        {
            _firstPackageTime = DateTime.Now;
        }

        if (_firstPackageTime.HasValue && totalCount > 0)
        {
            var elapsedTime = DateTime.Now - _firstPackageTime.Value;
            if (elapsedTime.TotalSeconds >= 1)
            {
                var efficiency = totalCount / elapsedTime.TotalHours;
                efficiencyItem.Value = efficiency.ToString("F0");
            }
            else
            {
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

        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}