using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Common.Models.Package;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Services;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Services.Dialogs;
using Serilog;
using ShanghaiModuleBelt.Models;
using ShanghaiModuleBelt.Services;
using SharedUI.Models;
using LockingService = ShanghaiModuleBelt.Services.LockingService;

namespace ShanghaiModuleBelt.ViewModels;

internal class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly ICameraService _cameraService;

    // 格口锁定状态字典
    private readonly Dictionary<int, bool> _chuteLockStatus = [];
    private readonly ChuteMappingService _chuteMappingService;
    private readonly ChutePackageRecordService _chutePackageRecordService;
    private readonly IDialogService _dialogService;
    private readonly LockingService _lockingService;
    private readonly IModuleConnectionService _moduleConnectionService;
    private readonly ISettingsService _settingsService;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly DispatcherTimer _timer;
    private string _currentBarcode = string.Empty;
    private bool _disposed;
    private SystemStatus _systemStatus = new();
    private BitmapSource? _currentImage;

    public MainWindowViewModel(IDialogService dialogService,
        ICameraService cameraService,
        PackageTransferService packageTransferService, ISettingsService settingsService,
        IModuleConnectionService moduleConnectionService,
        ChuteMappingService chuteMappingService,
        LockingService lockingService,
        ChutePackageRecordService chutePackageRecordService)
    {
        _dialogService = dialogService;
        _cameraService = cameraService;
        _settingsService = settingsService;
        _moduleConnectionService = moduleConnectionService;
        _chuteMappingService = chuteMappingService;
        _lockingService = lockingService;
        _chutePackageRecordService = chutePackageRecordService;
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
    public string CurrentBarcode
    {
        get => _currentBarcode;
        private set => SetProperty(ref _currentBarcode, value);
    }

    public BitmapSource? CurrentImage => _currentImage;

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
            Icon = "Scale24"
        });

        PackageInfoItems.Add(new PackageInfoItem
        {
            Label = "尺寸",
            Value = "0 × 0 × 0",
            Unit = "mm",
            Description = "长 × 宽 × 高",
            Icon = "Ruler24"
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
            Icon = "AlertCircle24"
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

            var config = _settingsService.LoadSettings<ModuleConfig>();
            var chuteNumber = await _chuteMappingService.GetChuteNumberAsync(package);

            if (chuteNumber == null)
            {
                Log.Warning("无法获取格口号，使用异常格口: {Barcode}", package.Barcode);
                package.SetStatus(PackageStatus.Error, "格口分配失败");
            }
            else
            {
                // 检查分配的格口是否被锁定
                if (IsChuteLocked(chuteNumber.Value))
                {
                    Log.Warning("分配的格口 {ChuteNumber} 已被锁定，重新分配到异常格口: {Barcode}",
                        chuteNumber.Value, package.Barcode);

                    // 记录原始分配的格口号
                    var originalChuteNumber = chuteNumber.Value;
                    package.SetChute(config.ExceptionChute, originalChuteNumber);

                    // 设置为错误状态
                    package.SetStatus(PackageStatus.Error, "格口已锁定，使用异常格口");
                    package.SetStatus(PackageStatus.Error,$"原格口 {originalChuteNumber} 已锁定");
                }
            }

            // 通知模组带服务处理包裹
            _moduleConnectionService.OnPackageReceived(package);

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
                    // 更新当前条码和图像
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
            package.SetStatus(PackageStatus.Error,$"处理失败：{ex.Message}");
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

    /// <summary>
    ///     处理锁格状态变更事件
    /// </summary>
    /// <param name="chuteNumber">格口号</param>
    /// <param name="isLocked">是否锁定</param>
    private async void OnChuteLockStatusChanged(int chuteNumber, bool isLocked)
    {
        try
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                // 更新格口锁定状态字典
                _chuteLockStatus[chuteNumber] = isLocked;

                // 记录状态变更
                Log.Information("格口 {ChuteNumber} 锁定状态变更为: {Status}",
                    chuteNumber, isLocked ? "锁定" : "解锁");
            });

            // 更新格口包裹记录服务中的锁定状态
            await _chutePackageRecordService.SetChuteLockStatusAsync(chuteNumber, isLocked);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理锁格状态变更事件时出错");
        }
    }

    /// <summary>
    ///     获取格口锁定状态
    /// </summary>
    /// <param name="chuteNumber">格口号</param>
    /// <returns>是否锁定</returns>
    private bool IsChuteLocked(int chuteNumber)
    {
        return _chuteLockStatus.TryGetValue(chuteNumber, out var isLocked) && isLocked;
    }

    /// <summary>
    ///     处理锁格设备连接状态变更事件
    /// </summary>
    /// <param name="isConnected">是否已连接</param>
    private void OnLockingDeviceConnectionChanged(bool isConnected)
    {
        UpdateLockingDeviceStatus(isConnected);
    }

    /// <summary>
    ///     更新锁格设备状态
    /// </summary>
    /// <param name="isConnected">是否已连接</param>
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

    protected virtual void Dispose(bool disposing)
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
}