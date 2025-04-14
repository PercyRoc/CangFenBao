using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Common.Models.Package;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Services;
using PlateTurnoverMachine.Models;
using PlateTurnoverMachine.Services;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Services.Dialogs;
using Serilog;
using SharedUI.Models;

namespace PlateTurnoverMachine.ViewModels;

internal class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly ICameraService _cameraService;
    private readonly IDialogService _dialogService;
    private readonly SortingService _sortingService;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly ITcpConnectionService _tcpConnectionService;
    private readonly DispatcherTimer _timer;
    private string _currentBarcode = string.Empty;
    private BitmapSource? _currentImage;
    private bool _disposed;
    private SystemStatus _systemStatus = new();
    private readonly ISettingsService _settingsService;
    private readonly IZtoSortingService _ztoSortingService;
    private int _historyIndexCounter = 0;

    public MainWindowViewModel(
        IDialogService dialogService,
        ICameraService cameraService,
        PackageTransferService packageTransferService,
        SortingService sortingService,
        ITcpConnectionService tcpConnectionService,
        ISettingsService settingsService,
        IZtoSortingService ztoSortingService)
    {
        _dialogService = dialogService;
        _cameraService = cameraService;
        _sortingService = sortingService;
        _tcpConnectionService = tcpConnectionService;
        _settingsService = settingsService;
        _ztoSortingService = ztoSortingService;

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

        // 订阅相机连接状态事件
        _cameraService.ConnectionChanged += OnCameraConnectionChanged;

        // 订阅触发光电连接状态事件
        _tcpConnectionService.TriggerPhotoelectricConnectionChanged += OnTriggerPhotoelectricConnectionChanged;

        // 订阅TCP模块连接状态变化
        _tcpConnectionService.TcpModuleConnectionChanged += OnTcpModuleConnectionChanged;

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

    #region Properties

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

    #endregion

    #region Private Methods

    private void ExecuteOpenSettings()
    {
        _dialogService.ShowDialog("SettingsDialog");
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        SystemStatus = SystemStatus.GetCurrentStatus();
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
            UpdateTcpModuleStatus();
            Log.Information("TCP模块 {IpAddress} 连接状态更新为：{Status}",
                e.Item1.IpAddress, e.Item2 ? "已连接" : "已断开");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理TCP模块连接状态变化时发生错误");
        }
    }

    private async void OnPackageInfo(PackageInfo package)
    {
        try
        {
            // 分配格口号
            var settings = _settingsService.LoadSettings<PlateTurnoverSettings>();
            var errorChuteNumber = settings.ErrorChute;

            // 处理未读包裹
            if (package.Barcode.Equals("noread", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }


            if (!string.IsNullOrEmpty(package.Barcode))
            {
                // 获取中通面单规则
                var billRule = await _ztoSortingService.GetBillRuleAsync();

                // 检查是否符合面单规则
                if (!string.IsNullOrEmpty(billRule.Pattern) &&
                    !System.Text.RegularExpressions.Regex.IsMatch(package.Barcode, billRule.Pattern))
                {
                    Log.Warning("包裹条码 {Barcode} 不符合中通面单规则，分配到异常口", package.Barcode);
                    package.SetChute(errorChuteNumber);
                    package.SetError("不符合中通面单规则");
                }
            }
            // 先调用中通接口获取分拣格口信息
            if (!string.IsNullOrEmpty(settings.ZtoPipelineCode) &&
                !string.IsNullOrEmpty(package.Barcode))
            {
                // Directly call the service. Subsequent logic handles the response.
                var sortingInfo = await _ztoSortingService.GetSortingInfoAsync(
                    package.Barcode,
                    settings.ZtoPipelineCode, // Use the local 'settings' variable
                    1, // 使用 0 或 package.PackageCount
                    settings.ZtoTrayCode, // Use the local 'settings' variable
                    (float)package.Weight);

                if (sortingInfo.SortPortCode.Count > 0)
                {
                    // 解析服务器返回的格口号
                    if (int.TryParse(sortingInfo.SortPortCode[0], out var chuteNumber))
                    {
                        package.SetChute(chuteNumber);
                        package.SetStatus(PackageStatus.Sorting); // 设置状态为 Sorting
                        Log.Information("从中通服务器获取到格口号：{ChuteNumber}，状态设置为 Sorting，包裹：{Barcode}", chuteNumber, package.Barcode);
                    }
                    else
                    {
                        Log.Warning("无法解析从中通获取的格口号 '{PortCode}'，分配到异常口 {ErrorChute}，包裹：{Barcode}", sortingInfo.SortPortCode[0], errorChuteNumber, package.Barcode);
                        package.SetChute(errorChuteNumber);
                        package.SetError($"从中通获取的格口号无效: {sortingInfo.SortPortCode[0]}");
                    }
                }
                else
                {
                    Log.Warning("中通未返回格口信息，分配到异常口 {ErrorChute}，包裹：{Barcode}", errorChuteNumber, package.Barcode);
                    package.SetChute(errorChuteNumber);
                    package.SetError("中通未返回格口信息");
                }
            }
            else if (string.IsNullOrEmpty(package.Barcode))
            {
                // 处理无条码情况（如果需要，但前面已有 noread 处理）
                Log.Warning("包裹无有效条码，分配到异常口 {ErrorChute}", errorChuteNumber);
                package.SetChute(errorChuteNumber);
                package.SetError("无有效条码");
            }
            // 如果未调用中通接口（例如未配置 PipelineCode），则可能需要默认逻辑或保持原状
            // 此处可以添加 else if (!string.IsNullOrEmpty(Settings.ZtoPipelineCode)) 来区分是否调用了中通
            // 如果没有调用 ZTO 且没有其他分配逻辑，包裹可能没有格口号，需要决定后续状态

            // 将包裹添加到分拣队列 (Runs after ZTO logic or if ZTO logic was skipped)
             _sortingService.EnqueuePackage(package);

            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    package.Index = Interlocked.Increment(ref _historyIndexCounter);
                    // 更新当前条码和图像
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
            Log.Error(ex, "处理包裹信息时发生错误：{Barcode}", package.Barcode);
            package.SetError($"处理失败：{ex.Message}");
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
            // Use ChuteNumber property (assuming it's available for getting)
            var chuteDisplay = package.ChuteNumber > 0 ? package.ChuteNumber.ToString() : "--";
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

            // 添加相机状态
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "相机",
                Status = "未连接",
                Icon = "Camera24",
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

            // 添加TCP模块汇总状态
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "TCP模块",
                Status = "0/0",
                Icon = "DeviceEq24",
                StatusColor = "#F44336"
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
            Application.Current.Dispatcher.Invoke(() =>
            {
                var status = DeviceStatuses.FirstOrDefault(static s => s.Name == "TCP模块");
                if (status == null) return;

                var totalModules = _tcpConnectionService.TcpModuleClients.Count;
                var connectedModules = _tcpConnectionService.TcpModuleClients.Count(static x => x.Value.Connected);

                status.Status = $"{connectedModules}/{totalModules}";
                status.StatusColor = connectedModules == totalModules ? "#4CAF50" :
                    connectedModules > 0 ? "#FFA500" : "#F44336";

                // 更新详细信息
                var details = new StringBuilder();
                foreach (var (config, client) in _tcpConnectionService.TcpModuleClients)
                    details.AppendLine($"{config.IpAddress}: {(client.Connected ? "已连接" : "已断开")}");

                Log.Debug("TCP模块状态更新为：{Status}, 已连接：{Connected}, 总数：{Total}",
                    status.Status, connectedModules, totalModules);
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新TCP模块状态时发生错误");
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
            Icon = "Scales24"
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
            Label = "格口",
            Value = "--",
            Description = "目标格口",
            Icon = "BoxMultiple24"
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
            Icon = "Alert24"
        });
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
            try
            {
                // 取消订阅事件
                _cameraService.ConnectionChanged -= OnCameraConnectionChanged;
                _tcpConnectionService.TriggerPhotoelectricConnectionChanged -= OnTriggerPhotoelectricConnectionChanged;
                _tcpConnectionService.TcpModuleConnectionChanged -= OnTcpModuleConnectionChanged;

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