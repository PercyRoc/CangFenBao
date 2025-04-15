using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Common.Models.Package;
using Common.Services.Audio;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Services;
using KuaiLv.Models.Settings.App;
using KuaiLv.Services.DWS;
using KuaiLv.Services.Warning;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Services.Dialogs;
using Serilog;
using SharedUI.Models;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Common.Data;

namespace KuaiLv.ViewModels;

internal class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly IAudioService _audioService;
    private readonly ICameraService _cameraService;
    private readonly IDialogService _dialogService;
    private readonly IDwsService _dwsService;
    private readonly ISettingsService _settingsService;
    private readonly IPackageDataService _packageDataService;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly DispatcherTimer _timer;
    private readonly IWarningLightService _warningLightService;
    private string _currentBarcode = string.Empty;
    private BitmapSource? _currentImage;
    private bool _disposed;
    private SystemStatus _systemStatus = new();
    private int _selectedScenario; // 默认为0，称重模式

    public MainWindowViewModel(
        IDialogService dialogService,
        ICameraService cameraService,
        IDwsService dwsService,
        IWarningLightService warningLightService,
        PackageTransferService packageTransferService,
        IAudioService audioService,
        ISettingsService settingsService,
        IPackageDataService packageDataService)
    {
        _dialogService = dialogService;
        _cameraService = cameraService;
        _dwsService = dwsService;
        _warningLightService = warningLightService;
        _audioService = audioService;
        _settingsService = settingsService;
        _packageDataService = packageDataService;

        // 初始化命令
        OpenSettingsCommand = new DelegateCommand(ExecuteOpenSettings);
        ResetWarningCommand = new DelegateCommand(ExecuteResetWarning);
        OpenHistoryCommand = new DelegateCommand(ExecuteOpenHistory);

        // 初始化使用场景列表
        InitializeScenarios();

        // 加载应用设置
        LoadAppSettings();

        // 初始化系统状态更新定时器
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        // 初始化设备状态
        InitializeDeviceStatuses();

        // 主动查询一次警示灯状态
        var warningLightStatus = DeviceStatuses.FirstOrDefault(static x => x.Name == "警示灯");
        if (warningLightStatus != null)
        {
            warningLightStatus.Status = _warningLightService.IsConnected ? "已连接" : "已断开";
            warningLightStatus.StatusColor = _warningLightService.IsConnected ? "#4CAF50" : "#F44336";
        }

        // 初始化统计数据
        InitializeStatisticsItems();

        // 初始化包裹信息
        InitializePackageInfoItems();

        // 订阅相机连接状态事件
        _cameraService.ConnectionChanged += OnCameraConnectionChanged;

        // 订阅警示灯连接状态事件
        _warningLightService.ConnectionChanged += OnWarningLightConnectionChanged;

        // 订阅图像流
        _subscriptions.Add(_cameraService.ImageStream
            .ObserveOn(TaskPoolScheduler.Default) // 使用任务池调度器
            .Subscribe(imageData =>
            {
                try
                {
                    Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
                    {
                        try
                        {
                            // 更新UI
                            CurrentImage = imageData;
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

        // 订阅包裹流
        _subscriptions.Add(packageTransferService.PackageStream
            .Subscribe(package => { Application.Current.Dispatcher.BeginInvoke(() => OnPackageInfo(package)); }));
    }

    #region Properties

    public DelegateCommand OpenSettingsCommand { get; }
    public DelegateCommand ResetWarningCommand { get; private set; }
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

    public int SelectedScenario
    {
        get => _selectedScenario;
        set
        {
            if (!SetProperty(ref _selectedScenario, value)) return;
            Log.Information("使用场景已更改为: {Scenario}", Scenarios[value]);
            // 保存设置
            SaveAppSettings();
        }
    }

    public ObservableCollection<string> Scenarios { get; } = [];
    public ObservableCollection<PackageInfo> PackageHistory { get; } = [];
    public ObservableCollection<StatisticsItem> StatisticsItems { get; } = [];
    public ObservableCollection<DeviceStatus> DeviceStatuses { get; } = [];
    public ObservableCollection<PackageInfoItem> PackageInfoItems { get; } = [];

    #endregion

    #region Private Methods

    private void LoadAppSettings()
    {
        try
        {
            var settings = _settingsService.LoadSettings<AppSettings>();
            SelectedScenario = settings.OperationMode;
            Log.Information("已加载应用设置，操作模式: {Mode}", Scenarios[SelectedScenario]);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载应用设置时发生错误");
        }
    }

    private void SaveAppSettings()
    {
        try
        {
            var settings = new AppSettings
            {
                OperationMode = SelectedScenario
            };
            _settingsService.SaveSettings(settings);
            Log.Information("已保存应用设置，操作模式: {Mode}", Scenarios[SelectedScenario]);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存应用设置时发生错误");
        }
    }

    private void InitializeScenarios()
    {
        Scenarios.Add("称重模式");
        Scenarios.Add("收货模式");
        Scenarios.Add("称重+收货模式");
        Log.Information("初始化使用场景列表完成，当前使用场景: {Scenario}", Scenarios[SelectedScenario]);
    }

    private void ExecuteOpenSettings()
    {
        _dialogService.ShowDialog("SettingsDialog");
    }

    private void ExecuteOpenHistory()
    {
        _dialogService.ShowDialog("HistoryDialog");
    }

    private async void ExecuteResetWarning()
    {
        try
        {
            // 关闭红灯并打开绿灯
            await _warningLightService.TurnOffRedLightAsync();
            await Task.Delay(100); // 短暂延时确保红灯完全关闭
            await _warningLightService.ShowGreenLightAsync();
            Log.Information("警示灯已重置：关闭红灯并打开绿灯");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "重置警示灯时发生错误");
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

            // 添加警示灯状态
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "警示灯",
                Status = "未连接",
                Icon = "Alert24",
                StatusColor = "#F44336" // 红色表示未连接
            });

            Log.Information("设备状态列表初始化完成，共 {Count} 个设备", DeviceStatuses.Count);
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
            Unit = "斤",
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

    private void OnWarningLightConnectionChanged(bool isConnected)
    {
        try
        {
            var warningLightStatus = DeviceStatuses.FirstOrDefault(static x => x.Name == "警示灯");
            if (warningLightStatus == null) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                warningLightStatus.Status = isConnected ? "已连接" : "已断开";
                warningLightStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新警示灯状态时发生错误");
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
                _warningLightService.ConnectionChanged -= OnWarningLightConnectionChanged;

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

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void UpdatePackageInfoItems(PackageInfo package)
    {
        var weightItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "重量");
        if (weightItem != null)
        {
            weightItem.Value = package.Weight.ToString("F2");
            weightItem.Unit = "斤";
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

        // 更新状态显示
        statusItem.Value = package.StatusDisplay; // 直接使用 StatusDisplay
        statusItem.Description = package.ErrorMessage ?? package.StatusDisplay; // 优先显示错误信息，否则显示状态文本

        // 根据状态设置颜色
        statusItem.StatusColor = package.Status switch
        {
            PackageStatus.Success
                => "#4CAF50", // 绿色表示成功
            PackageStatus.Error or PackageStatus.Timeout
                => "#F44336", // 红色表示错误、超时或失败
            _ => "#2196F3" // 其他状态（如进行中、等待中）使用蓝色或其他中性色
        };
    }

    private async void OnPackageInfo(PackageInfo package)
    {
        try
        {
            // 更新重量（千克转斤）
            package.SetWeight(package.Weight * 2);
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
                catch (Exception)
                {
                    UpdatePackageInfoItems(package);
                }
            });

            // 上报DWS
            var dwsResponse = await _dwsService.ReportPackageAsync(package);
            if (!dwsResponse.IsSuccess)
            {
                Log.Warning("DWS上报失败：{Message}", dwsResponse.Message);

                // 检查是否是网络离线的特殊情况
                if (dwsResponse.Code?.ToString() == "NETWORK_OFFLINE")
                {
                    Log.Information("网络离线，包裹已保存到离线存储：{Barcode}", package.Barcode);
                    // 设置离线状态和显示信息
                    package.SetStatus(PackageStatus.Offline, "网络离线，已保存到离线存储");
                }
                else
                {
                    // 其他失败情况，设置错误信息
                    package.SetStatus(PackageStatus.Error, $"DWS上报失败：{dwsResponse.Message}");
                }

                await _audioService.PlayPresetAsync(AudioType.SystemError);
            }
            else
            {
                await _audioService.PlayPresetAsync(AudioType.Success);
                // DWS 上报成功后，设置包裹状态为成功并显示服务器返回信息
                package.SetStatus(PackageStatus.Success, $"成功：{dwsResponse.Message}");
            }

            Application.Current.Dispatcher.Invoke(() => UpdatePackageInfoItems(package));

            Log.Debug("开始更新历史记录和统计数据，当前历史记录数量: {Count}", PackageHistory.Count);

            // 计算处理时间
            package.ProcessingTime = (DateTime.Now - package.CreateTime).TotalMilliseconds;
            try
            {
                await _packageDataService.AddPackageAsync(package);
                Log.Information("包裹记录已保存到数据库: {Barcode}, 状态: {Status}", package.Barcode, package.Status);
            }
            catch (Exception dbEx)
            {
                Log.Error(dbEx, "保存包裹记录到数据库时出错: {Barcode}", package.Barcode);
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    PackageHistory.Insert(0, package);
                    Log.Debug("历史记录更新后数量: {Count}", PackageHistory.Count);

                    while (PackageHistory.Count > 1000) // 保持最近1000条记录
                        PackageHistory.RemoveAt(PackageHistory.Count - 1);

                    // 更新统计数据
                    UpdateStatistics();
                    Log.Debug("统计数据更新完成");

                    // 强制通知UI刷新
                    RaisePropertyChanged(nameof(PackageHistory));
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "更新历史记录和统计信息时发生错误");
                    UpdatePackageInfoItems(package);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹信息时发生错误：{Barcode}", package.Barcode);
            Application.Current.Dispatcher.Invoke(() => UpdatePackageInfoItems(package));
        }
        finally
        {
            package.ReleaseImage();
        }
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

    #endregion
}