using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommonLibrary.Models;
using CommonLibrary.Models.Settings.Sort;
using CommonLibrary.Services;
using DeviceService;
using DeviceService.Camera;
using DeviceService.Camera.DaHua;
using Presentation_CommonLibrary.Models;
using Presentation_CommonLibrary.Services;
using Presentation_ZtCloudWarehous.Services;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Presentation_ZtCloudWarehous.ViewModels;

public class MainWindowViewModel: BindableBase, IDisposable
{
    private readonly ICameraService _cameraService;
    private readonly IDialogService _dialogService;
    private readonly IAudioService _audioService;
    private readonly IPendulumSortService _sortService;
    private readonly ISettingsService _settingsService;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly DispatcherTimer _timer;
    private string _currentBarcode = string.Empty;
    private BitmapSource? _currentImage;
    private int _currentPackageIndex;
    private bool _disposed;
    private SystemStatus _systemStatus = new();
    private bool _isInitialized;
    
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

    public MainWindowViewModel( IDialogService dialogService,
        ICameraService cameraService,
        PackageTransferService packageTransferService,
        IAudioService audioService, ISettingsService settingsService, IPendulumSortService sortService)
    {
        _dialogService = dialogService;
        _cameraService = cameraService;
        _audioService = audioService;
        _settingsService = settingsService;
        _sortService = sortService;
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
        _sortService.DeviceConnectionStatusChanged += OnDeviceConnectionStatusChanged;
        // 订阅包裹流
        _subscriptions.Add(packageTransferService.PackageStream
            .Subscribe(package => { Application.Current.Dispatcher.BeginInvoke(() => OnPackageInfo(package)); }));
        // 初始化摆轮分拣服务
        _ = InitializeSortServiceAsync();
    }
    private async Task InitializeSortServiceAsync()
    {
        try
        {
            if (_isInitialized) return;

            Log.Information("正在初始化摆轮分拣服务...");

            // 加载分拣配置
            var configuration = _settingsService.LoadSettings<SortConfiguration>("SortConfiguration");

            // 初始化分拣服务
            await _sortService.InitializeAsync(configuration);
            Log.Information("摆轮分拣服务初始化完成");

            // 启动分拣服务
            await _sortService.StartAsync();
            Log.Information("摆轮分拣服务已启动");

            // 更新设备状态
            await UpdateDeviceStatusesAsync();
            Log.Information("设备状态已更新");

            _isInitialized = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化摆轮分拣服务时发生错误");
            throw;
        }
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

            // 加载分拣配置
            var configuration = _settingsService.LoadSettings<SortConfiguration>("SortConfiguration");

            // 添加触发光电状态
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "触发光电",
                Status = "未连接",
                Icon = "Lightbulb24",
                StatusColor = "#F44336" // 红色表示未连接
            });

            // 添加分拣光电状态
            foreach (var photoelectric in configuration.SortingPhotoelectrics)
            {
                DeviceStatuses.Add(new DeviceStatus
                {
                    Name = photoelectric.Name,
                    Status = "未连接",
                    Icon = "Lightbulb24",
                    StatusColor = "#F44336" // 红色表示未连接
                });
            }

            Log.Debug("设备状态列表初始化完成，共 {Count} 个设备", DeviceStatuses.Count);
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

    private void OnCameraConnectionChanged(string deviceId, bool isConnected)
    {
        try
        {
            var cameraStatus = DeviceStatuses.FirstOrDefault(x => x.Name == "相机");
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
    private Task UpdateDeviceStatusesAsync()
    {
        try
        {
            // 更新相机状态
            var cameraStatus = DeviceStatuses.FirstOrDefault(x => x.Name == "相机");
            if (cameraStatus != null)
            {
                var isConnected = _cameraService.IsConnected;
                cameraStatus.Status = isConnected ? "已连接" : "已断开";
                cameraStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
            }

            // 更新分拣设备状态
            if (_sortService.IsRunning())
            {
                // 获取所有设备的连接状态
                var deviceStates = _sortService.GetAllDeviceConnectionStates();

                // 更新触发光电状态
                var triggerStatus = DeviceStatuses.FirstOrDefault(x => x.Name == "触发光电");
                if (triggerStatus != null && deviceStates.TryGetValue("触发光电", out var triggerConnected))
                {
                    triggerStatus.Status = triggerConnected ? "已连接" : "已断开";
                    triggerStatus.StatusColor = triggerConnected ? "#4CAF50" : "#F44336";
                }

                // 更新分检光电状态
                foreach (var status in DeviceStatuses.Where(x => x.Name != "相机" && x.Name != "触发光电"))
                {
                    if (!deviceStates.TryGetValue(status.Name, out var isConnected)) continue;
                    status.Status = isConnected ? "已连接" : "已断开";
                    status.StatusColor = isConnected ? "#4CAF50" : "#F44336";

                    Log.Debug("更新分拣光电状态: {Name} -> {Status}",
                        status.Name,
                        isConnected ? "已连接" : "已断开");
                }
            }
            else
            {
                // 如果服务未运行，将所有设备状态设置为未连接
                foreach (var status in DeviceStatuses.Where(x => x.Name != "相机"))
                {
                    status.Status = "未连接";
                    status.StatusColor = "#F44336";
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新设备状态时发生错误");
        }

        return Task.CompletedTask;
    }
    
    private static void UpdateImageDisplay(Image<Rgba32> image, Action<BitmapSource> imageUpdater)
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

    private async void OnPackageInfo(PackageInfo package)
    {
        try
        {
            // 设置包裹序号
            package.Index = Interlocked.Increment(ref _currentPackageIndex);
            Log.Information("收到包裹信息：{Barcode}, 序号：{Index}", package.Barcode, package.Index);
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
            _sortService.ProcessPackage(package);
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
            package.SetError($"处理失败：{ex.Message}");
        }
    }

    private void UpdatePackageInfoItems(PackageInfo package)
    {
        var weightItem = PackageInfoItems.FirstOrDefault(x => x.Label == "重量");
        if (weightItem != null)
        {
            weightItem.Value = package.Weight.ToString("F2");
            weightItem.Unit = "kg";
        }

        var sizeItem = PackageInfoItems.FirstOrDefault(x => x.Label == "尺寸");
        if (sizeItem != null)
        {
            sizeItem.Value = package.VolumeDisplay;
            sizeItem.Unit = "mm";
        }

        var timeItem = PackageInfoItems.FirstOrDefault(x => x.Label == "时间");
        if (timeItem != null)
        {
            timeItem.Value = package.CreateTime.ToString("HH:mm:ss");
            timeItem.Description = $"处理于 {package.CreateTime:yyyy-MM-dd}";
        }

        var statusItem = PackageInfoItems.FirstOrDefault(x => x.Label == "状态");
        if (statusItem == null) return;
        statusItem.Value = package.StatusDisplay;
        statusItem.Description = package.ErrorMessage ?? "处理状态";
    }

    private void UpdateStatistics()
    {
        var totalItem = StatisticsItems.FirstOrDefault(x => x.Label == "总包裹数");
        if (totalItem != null)
        {
            totalItem.Value = PackageHistory.Count.ToString();
            totalItem.Description = $"累计处理 {PackageHistory.Count} 个包裹";
        }

        var successItem = StatisticsItems.FirstOrDefault(x => x.Label == "成功数");
        if (successItem != null)
        {
            var successCount = PackageHistory.Count(p => string.IsNullOrEmpty(p.ErrorMessage));
            successItem.Value = successCount.ToString();
            successItem.Description = $"成功处理 {successCount} 个包裹";
        }

        var failedItem = StatisticsItems.FirstOrDefault(x => x.Label == "失败数");
        if (failedItem != null)
        {
            var failedCount = PackageHistory.Count(p => !string.IsNullOrEmpty(p.ErrorMessage));
            failedItem.Value = failedCount.ToString();
            failedItem.Description = $"失败处理 {failedCount} 个包裹";
        }

        var rateItem = StatisticsItems.FirstOrDefault(x => x.Label == "处理速率");
        if (rateItem == null) return;
        {
            var hourAgo = DateTime.Now.AddHours(-1);
            var hourlyCount = PackageHistory.Count(p => p.CreateTime > hourAgo);
            rateItem.Value = hourlyCount.ToString();
            rateItem.Description = $"最近一小时处理 {hourlyCount} 个";
        }
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
// 停止分拣服务
                if (_sortService.IsRunning())
                {
                    _sortService.StopAsync().Wait();
                    Log.Information("摆轮分拣服务已停止");
                }

                // 释放分拣服务资源
                if (_sortService is IDisposable disposableSortService)
                {
                    disposableSortService.Dispose();
                    Log.Information("摆轮分拣服务资源已释放");
                }

                // 取消订阅事件
                _sortService.DeviceConnectionStatusChanged -= OnDeviceConnectionStatusChanged;
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
}