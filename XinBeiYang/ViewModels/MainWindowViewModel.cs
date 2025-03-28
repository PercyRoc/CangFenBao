using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Common.Models.Package;
using Common.Services.Ui;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Services;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;
using SharedUI.Models;
using SixLabors.ImageSharp;
using XinBeiYang.Services;

namespace XinBeiYang.ViewModels;

internal class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly ICameraService _cameraService;
    private readonly IDialogService _dialogService;
    private readonly IPlcCommunicationService _plcCommunicationService;
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
        PackageTransferService packageTransferService,
        IPlcCommunicationService plcCommunicationService)
    {
        _dialogService = dialogService;
        _cameraService = cameraService;
        _plcCommunicationService = plcCommunicationService;

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

        // 订阅PLC连接状态事件
        _plcCommunicationService.ConnectionStatusChanged += OnPlcConnectionChanged;

        // 订阅包裹流
        _subscriptions.Add(packageTransferService.PackageStream
            .ObserveOn(Scheduler.CurrentThread)
            .Subscribe(package => 
            {
                try 
                {
                    _ = OnPackageInfo(package);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理包裹时发生错误");
                }
            }));

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

    public DelegateCommand OpenSettingsCommand { get; }
    public ObservableCollection<PackageInfo> PackageHistory { get; } = [];
    public ObservableCollection<StatisticsItem> StatisticsItems { get; } = [];
    public ObservableCollection<DeviceStatus> DeviceStatuses { get; } = [];
    public ObservableCollection<PackageInfoItem> PackageInfoItems { get; } = [];

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

    public string CurrentBarcode
    {
        get => _currentBarcode;
        private set => SetProperty(ref _currentBarcode, value);
    }

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

            // 添加PLC状态
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "PLC",
                Status = "未连接",
                Icon = "Chip24",
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

    private void OnCameraConnectionChanged(string cameraId, bool isConnected)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var cameraStatus = DeviceStatuses.FirstOrDefault(static s => s.Name == "相机");
            if (cameraStatus == null) return;

            cameraStatus.Status = isConnected ? "已连接" : "已断开";
            cameraStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
        });
    }

    private void OnPlcConnectionChanged(object? sender, bool isConnected)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            try
            {
                var plcStatus = DeviceStatuses.FirstOrDefault(static s => s.Name == "PLC");
                if (plcStatus == null) return;

                plcStatus.Status = isConnected ? "已连接" : "未连接";
                plcStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336"; // 绿色表示已连接，红色表示未连接
                Log.Information("PLC状态已更新：{Status}", plcStatus.Status);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "更新PLC状态栏时发生错误");
            }
        });
    }

    private async Task OnPackageInfo(PackageInfo package)
    {
        try
        {
            // 更新包裹序号和条码
            _currentPackageIndex++;
            package.Index = _currentPackageIndex;
            CurrentBarcode = package.Barcode;

            // 先更新UI显示
            Application.Current.Dispatcher.Invoke(() => 
            {
                UpdatePackageInfoItems(package);
            });

            // 发送上包请求
            var (isSuccess, isTimeout, commandId, packageId) = await _plcCommunicationService.SendUploadRequestAsync(
                package.Weight,
                (float)(package.Length ?? 0),
                (float)(package.Width ?? 0),
                (float)(package.Height ?? 0),
                package.Barcode,
                string.Empty,
                (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            if (!isSuccess)
            {
                if (isTimeout)
                {
                    Log.Warning("包裹 {Barcode}(序号:{Index}) 上包超时，CommandId={CommandId}", 
                        package.Barcode, package.Index, commandId);
                    package.SetError($"上包超时 (序号: {package.Index})");
                }
                else
                {
                    Log.Warning("包裹 {Barcode}(序号:{Index}) 上包请求被拒绝，CommandId={CommandId}", 
                        package.Barcode, package.Index, commandId);
                    package.SetError($"上包请求被拒绝 (序号: {package.Index})");
                }
            }
            else
            {
                // 更新包裹信息
                package.Status = PackageStatus.Processed;
                package.Information = $"上包成功 (序号: {package.Index}, 包裹流水号: {packageId})";
                Log.Information("包裹 {Barcode}(序号:{Index}) 上包成功，CommandId={CommandId}, 包裹流水号={PackageId}", 
                    package.Barcode, package.Index, commandId, packageId);
            }

            // 更新UI
            Application.Current.Dispatcher.Invoke(() => 
            {
                UpdatePackageInfoItems(package);
                UpdatePackageHistory(package);
                UpdateStatistics(package);
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹 {Barcode}(序号:{Index}) 时发生错误", package.Barcode, package.Index);
            package.SetError($"处理失败：{ex.Message} (序号: {package.Index})");
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

        var timeItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "时间");
        if (timeItem != null)
        {
            timeItem.Value = package.CreateTime.ToString("HH:mm:ss");
            timeItem.Description = $"处理于 {package.CreateTime:yyyy-MM-dd}";
        }

        var statusItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "状态");
        if (statusItem == null) return;

        if (string.IsNullOrEmpty(package.ErrorMessage))
        {
            statusItem.Value = "正常";
            statusItem.Description = $"处理成功 (序号: {package.Index})";
        }
        else
        {
            statusItem.Value = "异常";
            statusItem.Description = $"{package.ErrorMessage} (序号: {package.Index})";
        }
    }

    private void UpdatePackageHistory(PackageInfo package)
    {
        try
        {
            // 限制历史记录数量，保持最新的100条记录
            const int maxHistoryCount = 1000;

            // 如果Information为空，才设置默认信息
            if (string.IsNullOrEmpty(package.Information))
            {
                package.Information = string.IsNullOrEmpty(package.ErrorMessage) 
                    ? $"处理成功 (序号: {package.Index})" 
                    : $"{package.ErrorMessage} (序号: {package.Index})";
            }

            // 添加到历史记录开头
            PackageHistory.Insert(0, package);

            // 如果超出最大数量，移除多余的记录
            while (PackageHistory.Count > maxHistoryCount) PackageHistory.RemoveAt(PackageHistory.Count - 1);
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

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
            try
            {
                // 取消订阅事件
                _cameraService.ConnectionChanged -= OnCameraConnectionChanged;
                _plcCommunicationService.ConnectionStatusChanged -= OnPlcConnectionChanged;

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
}