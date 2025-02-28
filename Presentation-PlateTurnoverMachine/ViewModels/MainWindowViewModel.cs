using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommonLibrary.Models;
using DeviceService;
using DeviceService.Camera;
using Presentation_CommonLibrary.Models;
using Presentation_CommonLibrary.Services;
using Presentation_PlateTurnoverMachine.Models;
using Presentation_PlateTurnoverMachine.Services;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;
using SixLabors.ImageSharp;

namespace Presentation_PlateTurnoverMachine.ViewModels;

public class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly ICameraService _cameraService;
    private readonly ICustomDialogService _dialogService;
    private readonly Services.SortingService _sortingService;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly ITcpConnectionService _tcpConnectionService;
    private readonly DispatcherTimer _timer;
    private string _currentBarcode = string.Empty;
    private BitmapSource? _currentImage;
    private int _currentPackageIndex;
    private bool _disposed;
    private SystemStatus _systemStatus = new();

    public MainWindowViewModel(
        ICustomDialogService dialogService,
        ICameraService cameraService,
        PackageTransferService packageTransferService,
        Services.SortingService sortingService,
        ITcpConnectionService tcpConnectionService)
    {
        _dialogService = dialogService;
        _cameraService = cameraService;
        _sortingService = sortingService;
        _tcpConnectionService = tcpConnectionService;

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
            .ObserveOn(Scheduler.CurrentThread)
            .Subscribe(imageData =>
            {
                try
                {
                    var image = imageData.image;

                    // 创建内存流并将其所有权转移给BitmapImage
                    var memoryStream = new MemoryStream();
                    // 由于现在是灰度图像，需要调整保存格式
                    image.SaveAsPng(memoryStream); // 使用PNG格式保持灰度图像质量
                    memoryStream.Position = 0;

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            var bitmap = new BitmapImage();
                            bitmap.BeginInit();
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.StreamSource = memoryStream;
                            bitmap.EndInit();
                            bitmap.Freeze(); // 使图像可以跨线程访问

                            // 直接更新UI，不再绘制条码位置
                            CurrentImage = bitmap;
                        }
                        finally
                        {
                            memoryStream.Dispose();
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理图像数据时发生错误");
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

    private void OnCameraConnectionChanged(string cameraId, bool isConnected)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var cameraStatus = DeviceStatuses.FirstOrDefault(s => s.Name == "相机");
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
                var status = DeviceStatuses.FirstOrDefault(s => s.Name == "触发光电");
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

    private void OnPackageInfo(PackageInfo package)
    {
        try
        {
            package.Index = Interlocked.Increment(ref _currentPackageIndex);
            Log.Information("收到包裹信息：{Barcode}, 序号：{Index}", package.Barcode, package.Index);

            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    // 更新当前条码和图像
                    CurrentBarcode = package.Barcode;
                    // 更新实时包裹数据
                    UpdatePackageInfoItems(package);
                    // 将包裹添加到分拣队列
                    _sortingService.EnqueuePackage(package);

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
        var weightItem = PackageInfoItems.FirstOrDefault(x => x.Label == "重量");
        if (weightItem != null)
        {
            weightItem.Value = package.Weight.ToString(CultureInfo.InvariantCulture);
            weightItem.Unit = "kg";
        }

        var sizeItem = PackageInfoItems.FirstOrDefault(x => x.Label == "尺寸");
        if (sizeItem != null) sizeItem.Value = package.VolumeDisplay;

        var chuteItem = PackageInfoItems.FirstOrDefault(x => x.Label == "格口");
        if (chuteItem != null)
        {
            chuteItem.Value = package.ChuteName.ToString();
            chuteItem.Description = string.IsNullOrEmpty(package.ChuteName.ToString()) ? "等待分配..." : "目标格口";
        }

        var timeItem = PackageInfoItems.FirstOrDefault(x => x.Label == "时间");
        if (timeItem != null)
        {
            timeItem.Value = package.CreateTime.ToString("HH:mm:ss");
            timeItem.Description = $"处理于 {package.CreateTime:yyyy-MM-dd}";
        }

        var statusItem = PackageInfoItems.FirstOrDefault(x => x.Label == "状态");
        if (statusItem == null) return;
        statusItem.Value = string.IsNullOrEmpty(package.ErrorMessage) ? "正常" : "异常";
        statusItem.Description = string.IsNullOrEmpty(package.ErrorMessage) ? "处理成功" : package.ErrorMessage;
    }

    private void UpdatePackageHistory(PackageInfo package)
    {
        try
        {
            // 限制历史记录数量，保持最新的100条记录
            const int maxHistoryCount = 100;

            // 添加到历史记录开头
            PackageHistory.Insert(0, package);

            // 如果超出最大数量，移除多余的记录
            while (PackageHistory.Count > maxHistoryCount) PackageHistory.RemoveAt(PackageHistory.Count - 1);

            // 更新序号
            for (var i = 0; i < PackageHistory.Count; i++) PackageHistory[i].Index = i + 1;
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
            var totalItem = StatisticsItems.FirstOrDefault(x => x.Label == "总包裹数");
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
            var speedItem = StatisticsItems.FirstOrDefault(x => x.Label == "处理速率");
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
                Icon = "DeviceLaptop24",
                StatusColor = "#F44336",
                Description = "点击查看详情"
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
                var status = DeviceStatuses.FirstOrDefault(s => s.Name == "TCP模块");
                if (status == null) return;

                var totalModules = _tcpConnectionService.TcpModuleClients.Count;
                var connectedModules = _tcpConnectionService.TcpModuleClients.Count(x => x.Value.Connected);

                status.Status = $"{connectedModules}/{totalModules}";
                status.StatusColor = connectedModules == totalModules ? "#4CAF50" :
                    connectedModules > 0 ? "#FFA500" : "#F44336";

                // 更新详细信息
                var details = new StringBuilder();
                foreach (var (config, client) in _tcpConnectionService.TcpModuleClients)
                    details.AppendLine($"{config.IpAddress}: {(client.Connected ? "已连接" : "已断开")}");
                status.Description = details.ToString().TrimEnd();

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
            Icon = "AlertCircle24"
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