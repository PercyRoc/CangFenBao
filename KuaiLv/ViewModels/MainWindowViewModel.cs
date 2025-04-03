using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Common.Models.Package;
using Common.Services.Audio;
using Common.Services.Settings;
using Common.Services.Ui;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Camera.DaHua;
using DeviceService.DataSourceDevices.Services;
using KuaiLv.Models.Settings.App;
using KuaiLv.Services.DWS;
using KuaiLv.Services.Warning;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;
using SharedUI.Models;
using SixLabors.ImageSharp;

namespace KuaiLv.ViewModels;

internal class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly IAudioService _audioService;
    private readonly ICameraService _cameraService;
    private readonly IDialogService _dialogService;
    private readonly IDwsService _dwsService;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly DispatcherTimer _timer;
    private readonly IWarningLightService _warningLightService;
    private readonly ISettingsService _settingsService;
    private string _currentBarcode = string.Empty;
    private BitmapSource? _currentImage;
    private int _currentPackageIndex;
    private bool _disposed;
    private SystemStatus _systemStatus = new();
    private int _selectedScenario = 0; // 默认为0，称重模式

    public MainWindowViewModel(
        IDialogService dialogService,
        ICameraService cameraService,
        IDwsService dwsService,
        IWarningLightService warningLightService,
        PackageTransferService packageTransferService,
        IAudioService audioService,
        ISettingsService settingsService)
    {
        _dialogService = dialogService;
        _cameraService = cameraService;
        _dwsService = dwsService;
        _warningLightService = warningLightService;
        _audioService = audioService;
        _settingsService = settingsService;

        // 初始化命令
        OpenSettingsCommand = new DelegateCommand(ExecuteOpenSettings);
        ResetWarningCommand = new DelegateCommand(ExecuteResetWarning);
        OpenHistoryCommand = new DelegateCommand(ExecuteOpenHistory);
        ScenarioChangedCommand = new DelegateCommand<object>(ExecuteScenarioChanged);

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
        if (_cameraService is DahuaCameraService dahuaCamera)
            _subscriptions.Add(dahuaCamera.ImageStream
                .Subscribe(imageData =>
                {
                    try
                    {
                        Log.Debug("收到大华相机图像流数据，尺寸：{Width}x{Height}",
                            imageData.image.Width,
                            imageData.image.Height);

                        UpdateImageDisplay(imageData.image, bitmap =>
                        {
                            Log.Debug("从图像流更新CurrentImage");
                            CurrentImage = bitmap;
                        });
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "处理大华相机图像流数据时发生错误");
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
    public DelegateCommand<object> ScenarioChangedCommand { get; }

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
            if (SetProperty(ref _selectedScenario, value))
            {
                Log.Information("使用场景已更改为: {Scenario}", Scenarios[value]);
                // 保存设置
                SaveAppSettings();
            }
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

    private void ExecuteScenarioChanged(object parameter)
    {
        if (parameter is int index && index >= 0 && index < Scenarios.Count)
        {
            SelectedScenario = index;
            Log.Information("切换使用场景为: {Scenario}", Scenarios[index]);
        }
    }

    private void ExecuteOpenSettings()
    {
        _dialogService.ShowDialog("SettingsDialog");
    }

    private void ExecuteOpenHistory()
    {
        _dialogService.ShowDialog("HistoryWindow");
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

    private static void UpdateImageDisplay(Image image, Action<BitmapSource> imageUpdater)
    {
        try
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    using var memoryStream = new MemoryStream();
                    image.SaveAsJpeg(memoryStream);
                    memoryStream.Position = 0;

                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = memoryStream;
                    bitmap.EndInit();
                    bitmap.Freeze(); // 使图像可以跨线程访问

                    imageUpdater(bitmap);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "在UI线程更新图像显示时发生错误");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新图像显示时发生错误");
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
        if (string.IsNullOrEmpty(package.ErrorMessage))
        {
            statusItem.Value = "正常";
            statusItem.Description = package.Information ?? "处理状态";
            statusItem.StatusColor = "#4CAF50"; // 绿色表示正常
        }
        else
        {
            statusItem.Value = "异常";
            statusItem.Description = package.ErrorMessage;
            statusItem.StatusColor = "#F44336"; // 红色表示异常
        }
    }

    private async void OnPackageInfo(PackageInfo package)
    {
        try
        {
            // 设置包裹序号
            package.Index = Interlocked.Increment(ref _currentPackageIndex);
            Log.Information("收到包裹信息：{Barcode}, 序号：{Index}", package.Barcode, package.Index);
            package.Weight *= 2;

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
                    package.SetError($"更新UI失败：{ex.Message}");
                    UpdatePackageInfoItems(package);
                }
            });

            // 上报DWS
            var dwsResponse = await _dwsService.ReportPackageAsync(package);
            if (!dwsResponse.IsSuccess)
            {
                Log.Warning("DWS上报失败：{Message}", dwsResponse.Message);
                package.SetError($"DWS上报失败：{dwsResponse.Message}");
                await _audioService.PlayPresetAsync(AudioType.SystemError);

                // 更新UI显示错误状态
                Application.Current.Dispatcher.Invoke(() => UpdatePackageInfoItems(package));
            }
            else
            {
                await _audioService.PlayPresetAsync(AudioType.Success);
                // 更新UI显示成功状态
                Application.Current.Dispatcher.Invoke(() =>
                {
                    package.StatusDisplay = "成功";
                    // Information属性在DwsService中已经设置，这里只需要更新UI
                    UpdatePackageInfoItems(package);
                });
            }

            Log.Debug("开始更新历史记录和统计数据，当前历史记录数量: {Count}", PackageHistory.Count);
            
            // 计算处理时间
            package.ProcessingTime = (DateTime.Now - package.CreateTime).TotalMilliseconds;
            
            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    // 创建PackageInfo的新实例，避免引用原始对象导致的问题
                    var packageCopy = new PackageInfo
                    {
                        Index = package.Index,
                        Barcode = package.Barcode,
                        Weight = package.Weight,
                        Length = package.Length,
                        Width = package.Width,
                        Height = package.Height,
                        Volume = package.Volume,
                        StatusDisplay = package.StatusDisplay,
                        ProcessingTime = package.ProcessingTime,
                        CreateTime = package.CreateTime,
                        Information = package.Information,
                        ErrorMessage = package.ErrorMessage
                    };
                    
                    // 更新历史记录
                    Log.Debug("添加包裹到历史记录: {Barcode}, 序号: {Index}", packageCopy.Barcode, packageCopy.Index);
                    
                    // 在UI线程上更新ObservableCollection
                    PackageHistory.Insert(0, packageCopy);
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
                    package.SetError($"更新统计失败：{ex.Message}");
                    UpdatePackageInfoItems(package);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹信息时发生错误：{Barcode}", package.Barcode);
            package.SetError($"处理失败：{ex.Message}");
            // 确保在发生错误时更新UI显示
            Application.Current.Dispatcher.Invoke(() => UpdatePackageInfoItems(package));
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