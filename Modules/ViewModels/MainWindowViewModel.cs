using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Common.Models.Package;
using Common.Models.Settings.ChuteRules;
using Common.Services.Settings;
using Common.Services.Ui;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Services;
using Serilog;
using ShanghaiModuleBelt.Models;
using ShanghaiModuleBelt.Services;
using ShanghaiModuleBelt.Views;
using SharedUI.Models;

namespace ShanghaiModuleBelt.ViewModels;

internal class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly ICameraService _cameraService;

    // 格口统计计数器 - 维护完整的统计数据
    private readonly Dictionary<int, int> _chutePackageCount = new();

    // 格口锁定状态字典
    private readonly ChuteMappingService _chuteMappingService;
    private readonly IDialogService _dialogService;
    private readonly LockingService _lockingService;
    private readonly IModuleConnectionService _moduleConnectionService;
    private readonly INotificationService _notificationService;
    private readonly ChutePackageRecordService _chutePackageRecordService;
    private readonly ISettingsService _settingsService;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly DispatcherTimer _timer;
    private string _currentBarcode = string.Empty;
    private bool _disposed;
    private SystemStatus _systemStatus = new();

    public MainWindowViewModel(IDialogService dialogService,
        INotificationService notificationService,
        ICameraService cameraService,
        PackageTransferService packageTransferService, ISettingsService settingsService,
        IModuleConnectionService moduleConnectionService,
        ChuteMappingService chuteMappingService,
        LockingService lockingService,
        ChutePackageRecordService chutePackageRecordService)
    {
        _dialogService = dialogService;
        _notificationService = notificationService;
        _cameraService = cameraService;
        _settingsService = settingsService;
        _moduleConnectionService = moduleConnectionService;
        _chuteMappingService = chuteMappingService;
        _lockingService = lockingService;
        _chutePackageRecordService = chutePackageRecordService;
        OpenSettingsCommand = new DelegateCommand(ExecuteOpenSettings);
        ShowChuteStatisticsCommand = new DelegateCommand(ExecuteShowChuteStatistics);

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

        // 订阅模组带连接状态事件
        _moduleConnectionService.ConnectionStateChanged += OnModuleConnectionChanged;

        // 订阅锁格状态变更事件
        _lockingService.ChuteLockStatusChanged += OnChuteLockStatusChanged;

        // 订阅锁格设备连接状态变更事件
        _lockingService.ConnectionStatusChanged += OnLockingDeviceConnectionChanged;
        // 订阅包裹流
        _subscriptions.Add(packageTransferService.PackageStream
            .Subscribe(package => { Application.Current.Dispatcher.BeginInvoke(() => OnPackageInfo(package)); }));

        // 初始检查锁格设备状态
        UpdateLockingDeviceStatus(_lockingService.IsConnected());
    }

    public DelegateCommand OpenSettingsCommand { get; }
    public DelegateCommand ShowChuteStatisticsCommand { get; }
    public string CurrentBarcode
    {
        get => _currentBarcode;
        private set => SetProperty(ref _currentBarcode, value);
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

    private void ExecuteShowChuteStatistics()
    {
        try
        {
            var dialog = new ChuteStatisticsDialog();
            var viewModel = new ChuteStatisticsDialogViewModel(_notificationService);

            // 更新统计数据
            viewModel.UpdateStatistics(_chutePackageCount);

            // 设置数据上下文
            dialog.DataContext = viewModel;

            // 设置刷新动作的处理逻辑
            viewModel.RefreshAction = () =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    viewModel.UpdateStatistics(_chutePackageCount);
                });
            };

            // 设置父窗口并显示对话框
            dialog.Owner = Application.Current.MainWindow;
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "显示格口统计对话框时发生错误");
        }
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

            // 添加模组带状态
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "模组带",
                Status = "未连接",
                Icon = "ArrowSort24",
                StatusColor = "#F44336" // 红色表示未连接
            });

            // 添加锁格设备状态
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "锁格设备",
                Status = "未连接",
                Icon = "Lock24",
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
            "成功数",
            "0",
            "个",
            "处理成功的包裹数量",
            "CheckmarkCircle24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            "失败数",
            "0",
            "个",
            "处理失败的包裹数量",
            "ErrorCircle24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            "处理速率",
            "0",
            "个/小时",
            "每小时处理包裹数量",
            "ArrowTrendingLines24"
        ));
    }

    private void InitializePackageInfoItems()
    {
        PackageInfoItems.Add(new PackageInfoItem(
            "重量",
            "0.00",
            "kg",
            "包裹重量",
            "Scales24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "尺寸",
            "0 × 0 × 0",
            "mm",
            "长 × 宽 × 高",
            "Ruler24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "时间",
            "--:--:--",
            "",
            "处理时间",
            "Timer24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "状态",
            "等待",
            "",
            "处理状态",
            "AlertCircle24"
        ));
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

    private void OnModuleConnectionChanged(object? sender, bool isConnected)
    {
        try
        {
            var moduleStatus = DeviceStatuses.FirstOrDefault(static x => x.Name == "模组带");
            if (moduleStatus == null) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                moduleStatus.Status = isConnected ? "已连接" : "已断开";
                moduleStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新模组带状态时发生错误");
        }
    }

    private async void OnPackageInfo(PackageInfo package)
    {
        try
        {
            Log.Information("收到包裹信息: {Barcode}, 序号={Index}", package.Barcode, package.Index);
            // 从 ChuteSettings 获取格口配置
            var moduleConfig = _settingsService.LoadSettings<ModuleConfig>();

            // 检查条码是否包含异常字符（|或逗号）
            if (package.Barcode.Contains('|') || package.Barcode.Contains(','))
            {
                Log.Warning("条码包含异常字符（|或,），分到异常格口: {Barcode}", package.Barcode);
                package.SetChute(moduleConfig.ExceptionChute);
                package.SetStatus(PackageStatus.Error, "条码包含异常字符");
            }
            else
            {
                // 根据条码查找匹配的格口
                var chuteNumber = await _chuteMappingService.GetChuteNumberAsync(package);

                if (chuteNumber == null)
                {
                    Log.Warning("无法获取格口号，使用异常格口: {Barcode}", package.Barcode);
                    package.SetChute(moduleConfig.ExceptionChute); // 使用 ChuteSettings 中的异常格口
                    package.SetStatus(PackageStatus.Error, "格口分配失败");
                }
                else
                {
                    // 设置包裹的格口
                    package.SetChute(chuteNumber.Value); // 不再有原始格口的概念，因为直接匹配规则
                }
            }

            // 检查格口是否被锁定
            if (_chutePackageRecordService.IsChuteLocked(package.ChuteNumber))
            {
                Log.Warning("格口 {ChuteNumber} 已锁定，将包裹 {Barcode} 分到异常格口。", package.ChuteNumber, package.Barcode);
                package.SetChute(moduleConfig.ExceptionChute);
                package.SetStatus(PackageStatus.Error, "格口已锁定");
            }

            // 通知模组带服务处理包裹
            _moduleConnectionService.OnPackageReceived(package);

            // 更新格口统计计数器
            if (!_chutePackageCount.TryAdd(package.ChuteNumber, 1))
            {
                _chutePackageCount[package.ChuteNumber]++;
            }

            // 如果没有错误，设置为正常状态
            if (string.IsNullOrEmpty(package.ErrorMessage))
            {
                package.SetStatus(PackageStatus.Success, "正常");
            }
            // 更新UI
            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    // 更新当前条码
                    CurrentBarcode = package.Barcode;
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
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹信息时发生错误：{Barcode}", package.Barcode);
            package.SetStatus(PackageStatus.Error, $"处理失败：{ex.Message}");
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

        var sizeItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "尺寸");
        if (sizeItem != null)
        {
            sizeItem.Value = package.VolumeDisplay;
            sizeItem.Unit = "mm";
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
        statusItem.Description = package.ErrorMessage ?? "处理状态";
    }

    private void UpdateStatistics()
    {
        var totalItem = StatisticsItems.FirstOrDefault(static x => x.Label == "总包裹数");
        if (totalItem != null)
        {
            totalItem.Value = PackageHistory.Count.ToString();
            totalItem.Description = $"累计处理 {PackageHistory.Count} 个包裹";
        }

        var successItem = StatisticsItems.FirstOrDefault(static x => x.Label == "成功数");
        if (successItem != null)
        {
            var successCount = PackageHistory.Count(static p => string.IsNullOrEmpty(p.ErrorMessage));
            successItem.Value = successCount.ToString();
            successItem.Description = $"成功处理 {successCount} 个包裹";
        }

        var failedItem = StatisticsItems.FirstOrDefault(static x => x.Label == "失败数");
        if (failedItem != null)
        {
            var failedCount = PackageHistory.Count(static p => !string.IsNullOrEmpty(p.ErrorMessage));
            failedItem.Value = failedCount.ToString();
            failedItem.Description = $"失败处理 {failedCount} 个包裹";
        }

        var rateItem = StatisticsItems.FirstOrDefault(static x => x.Label == "处理速率");
        if (rateItem == null) return;

        {
            var hourAgo = DateTime.Now.AddHours(-1);
            var hourlyCount = PackageHistory.Count(p => p.CreateTime > hourAgo);
            rateItem.Value = hourlyCount.ToString();
            rateItem.Description = $"最近一小时处理 {hourlyCount} 个";
        }
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
            try
            {
                // 停止定时器
                _timer.Stop();

                // 取消事件订阅
                _cameraService.ConnectionChanged -= OnCameraConnectionChanged;
                _moduleConnectionService.ConnectionStateChanged -= OnModuleConnectionChanged;
                _lockingService.ChuteLockStatusChanged -= OnChuteLockStatusChanged;
                _lockingService.ConnectionStatusChanged -= OnLockingDeviceConnectionChanged;

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

    /// <summary>
    ///     更新锁格设备状态
    /// </summary>
    /// <param name="isConnected"></param>
    private void UpdateLockingDeviceStatus(bool isConnected)
    {
        try
        {
            var lockingDeviceStatus = DeviceStatuses.FirstOrDefault(static x => x.Name == "锁格设备");
            if (lockingDeviceStatus == null) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                lockingDeviceStatus.Status = isConnected ? "已连接" : "已断开";
                lockingDeviceStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新锁格设备状态时发生错误");
        }
    }

    /// <summary>
    ///     处理锁格状态变更事件
    /// </summary>
    /// <param name="chuteNumber">格口号</param>
    /// <param name="isLocked">是否锁定</param>
    private async void OnChuteLockStatusChanged(int chuteNumber, bool isLocked)
    {
        // 如果格口被锁定，则通知格口包裹记录服务进行数据上传和清空
        if (isLocked)
        {
            await _chutePackageRecordService.SetChuteLockStatusAsync(chuteNumber, true);
        }
    }

    /// <summary>
    ///     处理锁格设备连接状态变更事件
    /// </summary>
    /// <param name="isConnected">是否已连接</param>
    private void OnLockingDeviceConnectionChanged(bool isConnected)
    {
        UpdateLockingDeviceStatus(isConnected);
    }
}