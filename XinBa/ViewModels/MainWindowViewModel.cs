using System.Collections.ObjectModel;
using System.IO;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Common.Models;
using Common.Models.Package;
using Common.Services.Ui;
using Presentation_XinBa.Services;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;
using SharedUI.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace Presentation_XinBa.ViewModels;

/// <summary>
///     主窗口视图模型
/// </summary>
public class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly IApiService _apiService;
    private readonly TcpCameraService? _cameraService;
    private readonly IDialogService _dialogService;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly DispatcherTimer _timer;
    private string _currentBarcode = string.Empty;
    private string _currentEmployeeInfo = string.Empty;
    private BitmapSource? _currentImage;
    private bool _disposed;
    private int _failedPackages;
    private DateTime _lastRateCalculationTime = DateTime.Now;
    private int _packagesInLastHour;
    private int _processingRate;
    private int _successPackages;
    private SystemStatus _systemStatus = new();
    private int _totalPackages;

    public MainWindowViewModel(
        IDialogService dialogService,
        TcpCameraService cameraService,
        IApiService apiService)
    {
        _dialogService = dialogService;
        _cameraService = cameraService;
        _apiService = apiService;

        // Initialize commands
        OpenSettingsCommand = new DelegateCommand(ExecuteOpenSettings);
        LogoutCommand = new DelegateCommand(ExecuteLogout);

        // Initialize system status update timer
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        // Initialize device status
        InitializeDeviceStatuses();

        // Initialize statistics data
        InitializeStatisticsItems();

        // Initialize package information
        InitializePackageInfoItems();

        // 更新当前登录员工信息 - 初始调用
        _ = UpdateCurrentEmployeeInfo();

        // 订阅相机连接状态事件
        if (_cameraService != null)
        {
            Log.Debug("准备订阅相机连接状态事件，相机服务类型: {ServiceType}", _cameraService.GetType().FullName);
            _cameraService.ConnectionChanged += OnCameraConnectionChanged;
            Log.Information("已成功订阅相机连接状态事件");

            // 订阅包裹流
            var packageSubscription = _cameraService.PackageStream
                .ObserveOn(TaskPoolScheduler.Default)
                .Subscribe(OnPackageReceived);
            _subscriptions.Add(packageSubscription);
            Log.Information("已成功订阅包裹流");
        }
        else
        {
            Log.Warning("相机服务为null，无法订阅连接状态事件和包裹流");
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     登出请求事件
    /// </summary>
    public event EventHandler? LogoutRequested;

    private void ExecuteOpenSettings()
    {
        _dialogService.ShowDialog("SettingsDialog");
    }

    /// <summary>
    ///     执行登出
    /// </summary>
    private async void ExecuteLogout()
    {
        try
        {
            var result = await _dialogService.ShowIconConfirmAsync(
                "确定要登出当前用户吗？",
                "登出确认",
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            // 先触发登出请求事件，创建登录窗口
            Log.Information("用户确认登出，触发登出请求事件");
            LogoutRequested?.Invoke(this, EventArgs.Empty);

            // 执行登出API调用
            Log.Information("开始调用登出API");
            var success = await _apiService.LogoutAsync();

            if (!success)
            {
                Log.Warning("用户登出失败");
                await _dialogService.ShowErrorAsync("登出失败，请稍后重试", "错误");
            }
            else
            {
                Log.Information("用户登出成功");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "执行登出过程中发生错误");
            await _dialogService.ShowErrorAsync($"登出过程中发生错误: {ex.Message}", "错误");
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        SystemStatus = SystemStatus.GetCurrentStatus();

        // 每小时更新一次处理速率
        var now = DateTime.Now;
        if (!((now - _lastRateCalculationTime).TotalMinutes >= 1)) return;
        UpdateProcessingRate();
        _lastRateCalculationTime = now;
    }

    private void InitializeDeviceStatuses()
    {
        try
        {
            Log.Debug("Starting to initialize device status list");

            // Add camera status
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "Camera",
                Status = "Not Connected",
                Icon = "Camera24",
                StatusColor = "#F44336" // Red indicates not connected
            });

            Log.Information("Device status list initialization completed, total {Count} devices", DeviceStatuses.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error occurred while initializing device status list");
        }
    }

    private void InitializeStatisticsItems()
    {
        StatisticsItems.Add(new StatisticsItem
        {
            Label = "Total Packages",
            Value = "0",
            Unit = "pcs",
            Description = "Total number of processed packages",
            Icon = "BoxMultiple24"
        });

        StatisticsItems.Add(new StatisticsItem
        {
            Label = "Success Count",
            Value = "0",
            Unit = "pcs",
            Description = "Number of successfully processed packages",
            Icon = "CheckmarkCircle24"
        });

        StatisticsItems.Add(new StatisticsItem
        {
            Label = "Failure Count",
            Value = "0",
            Unit = "pcs",
            Description = "Number of failed packages",
            Icon = "ErrorCircle24"
        });

        StatisticsItems.Add(new StatisticsItem
        {
            Label = "Processing Rate",
            Value = "0",
            Unit = "pcs/hour",
            Description = "Packages processed per hour",
            Icon = "ArrowTrendingLines24"
        });
    }

    private void InitializePackageInfoItems()
    {
        PackageInfoItems.Add(new PackageInfoItem
        {
            Label = "Weight",
            Value = "0.00",
            Unit = "kg",
            Description = "Package weight",
            Icon = "Scale24"
        });

        PackageInfoItems.Add(new PackageInfoItem
        {
            Label = "Dimensions",
            Value = "0 × 0 × 0",
            Unit = "mm",
            Description = "Length × Width × Height",
            Icon = "Ruler24"
        });

        PackageInfoItems.Add(new PackageInfoItem
        {
            Label = "Time",
            Value = "--:--:--",
            Description = "Processing time",
            Icon = "Timer24"
        });

        PackageInfoItems.Add(new PackageInfoItem
        {
            Label = "Status",
            Value = "Waiting",
            Description = "Processing status",
            Icon = "AlertCircle24"
        });
    }

    /// <summary>
    ///     更新当前登录员工信息
    /// </summary>
    public Task UpdateCurrentEmployeeInfo()
    {
        try
        {
            var employeeId = _apiService.GetCurrentEmployeeId();
            if (employeeId.HasValue)
            {
                CurrentEmployeeInfo = $"Employee ID: {employeeId.Value}";
                Log.Information("已更新当前登录员工信息: {EmployeeId}", employeeId.Value);
            }
            else
            {
                CurrentEmployeeInfo = "未登录";
                Log.Warning("未检测到登录员工信息");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新当前登录员工信息时出错");
            CurrentEmployeeInfo = "获取员工信息出错";
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///     处理接收到的包裹信息
    /// </summary>
    /// <param name="package">包裹信息</param>
    private async void OnPackageReceived(PackageInfo package)
    {
        try
        {
            Log.Debug("接收到包裹信息: 条码={Barcode}, 重量={Weight}kg", package.Barcode, package.Weight);

            // 使用Dispatcher确保在UI线程上更新
            Application.Current.Dispatcher.Invoke(() =>
            {
                // 更新当前条码
                CurrentBarcode = package.Barcode;

                // 更新包裹信息项
                UpdatePackageInfoItems(package);

                // 更新统计信息
                UpdateStatistics(package);

                // 添加到包裹历史
                AddToPackageHistory(package);

                // 更新当前图像
                UpdateCurrentImage(package);
            });

            // 提交商品尺寸
            if (package is { Length: not null, Width: not null, Height: not null })
            {
                // 准备图片数据
                var photoData = new List<byte[]>();

                // 如果有图片，转换为字节数组
                if (package.Image != null)
                    try
                    {
                        using var ms = new MemoryStream();
                        await package.Image.SaveAsync(ms, new JpegEncoder());
                        photoData.Add(ms.ToArray());
                        Log.Debug("图片已转换为字节数组");
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "转换图片数据失败");
                    }

                // 提交尺寸信息
                var success = await _apiService.SubmitDimensionsAsync(
                    package.Barcode,
                    package.Height.Value.ToString("F2"),
                    package.Length.Value.ToString("F2"),
                    package.Width.Value.ToString("F2"),
                    (package.Weight * 1000).ToString("F2"), // 转换为克 (kg -> g)
                    photoData
                );

                if (success)
                    Log.Information("商品尺寸提交成功: Barcode={Barcode}", package.Barcode);
                else
                    Log.Warning("商品尺寸提交失败: Barcode={Barcode}", package.Barcode);
            }
            else
            {
                Log.Warning("包裹缺少尺寸信息，无法提交: Barcode={Barcode}", package.Barcode);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹信息时出错: 条码={Barcode}", package.Barcode);
        }
    }

    /// <summary>
    ///     更新包裹信息项
    /// </summary>
    /// <param name="package">包裹信息</param>
    private void UpdatePackageInfoItems(PackageInfo package)
    {
        try
        {
            // 更新重量
            var weightItem = PackageInfoItems.FirstOrDefault(p => p.Label == "Weight");
            if (weightItem != null) weightItem.Value = $"{package.Weight:F2}";

            // 更新尺寸
            var dimensionsItem = PackageInfoItems.FirstOrDefault(p => p.Label == "Dimensions");
            if (dimensionsItem != null)
            {
                var length = package.Length ?? 0;
                var width = package.Width ?? 0;
                var height = package.Height ?? 0;
                dimensionsItem.Value = $"{length:F0} × {width:F0} × {height:F0}";
            }

            // 更新时间
            var timeItem = PackageInfoItems.FirstOrDefault(p => p.Label == "Time");
            if (timeItem != null) timeItem.Value = package.TriggerTimestamp.ToString("HH:mm:ss");

            // 更新状态
            var statusItem = PackageInfoItems.FirstOrDefault(p => p.Label == "Status");
            if (statusItem != null) statusItem.Value = GetStatusDisplayText(package.Status);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新包裹信息项时出错");
        }
    }

    /// <summary>
    ///     获取状态显示文本
    /// </summary>
    /// <param name="status">包裹状态</param>
    /// <returns>状态显示文本</returns>
    private static string GetStatusDisplayText(PackageStatus status)
    {
        return status switch
        {
            PackageStatus.Created => "已创建",
            PackageStatus.Measuring => "测量中",
            PackageStatus.MeasureSuccess => "测量成功",
            PackageStatus.MeasureFailed => "测量失败",
            PackageStatus.Weighing => "称重中",
            PackageStatus.WeighSuccess => "称重成功",
            PackageStatus.WeighFailed => "称重失败",
            PackageStatus.Sorting => "分拣中",
            PackageStatus.SortSuccess => "分拣完成",
            PackageStatus.SortFailed => "分拣失败",
            _ => "未知状态"
        };
    }

    /// <summary>
    ///     更新统计信息
    /// </summary>
    /// <param name="package">包裹信息</param>
    private void UpdateStatistics(PackageInfo package)
    {
        try
        {
            // 更新总包裹数
            _totalPackages++;
            var totalItem = StatisticsItems.FirstOrDefault(s => s.Label == "Total Packages");
            if (totalItem != null) totalItem.Value = _totalPackages.ToString();

            // 更新成功/失败数量
            var isSuccess = package.Status is PackageStatus.MeasureSuccess or PackageStatus.WeighSuccess
                or PackageStatus.SortSuccess;

            if (isSuccess)
            {
                _successPackages++;
                var successItem = StatisticsItems.FirstOrDefault(s => s.Label == "Success Count");
                if (successItem != null) successItem.Value = _successPackages.ToString();
            }
            else
            {
                _failedPackages++;
                var failureItem = StatisticsItems.FirstOrDefault(s => s.Label == "Failure Count");
                if (failureItem != null) failureItem.Value = _failedPackages.ToString();
            }

            // 更新处理速率计算
            _packagesInLastHour++;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新统计信息时出错");
        }
    }

    /// <summary>
    ///     更新处理速率
    /// </summary>
    private void UpdateProcessingRate()
    {
        try
        {
            // 计算每小时处理速率
            _processingRate = _packagesInLastHour * 60; // 每分钟的包裹数 * 60 = 每小时的包裹数
            _packagesInLastHour = 0; // 重置计数器

            var rateItem = StatisticsItems.FirstOrDefault(s => s.Label == "Processing Rate");
            if (rateItem != null) rateItem.Value = _processingRate.ToString();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新处理速率时出错");
        }
    }

    /// <summary>
    ///     添加到包裹历史
    /// </summary>
    /// <param name="package">包裹信息</param>
    private void AddToPackageHistory(PackageInfo package)
    {
        try
        {
            // 创建一个新的包裹对象以避免引用问题
            var newPackage = new PackageInfo
            {
                Barcode = package.Barcode,
                Weight = package.Weight,
                Length = package.Length,
                Width = package.Width,
                Height = package.Height,
                TriggerTimestamp = package.TriggerTimestamp,
                Status = package.Status,
                CreateTime = DateTime.Now
            };

            // 添加到历史记录的开头
            PackageHistory.Insert(0, newPackage);

            // 限制历史记录数量
            const int maxHistoryItems = 100;
            while (PackageHistory.Count > maxHistoryItems) PackageHistory.RemoveAt(PackageHistory.Count - 1);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "添加到包裹历史时出错");
        }
    }

    /// <summary>
    ///     更新当前图像
    /// </summary>
    /// <param name="package">包裹信息</param>
    private void UpdateCurrentImage(PackageInfo package)
    {
        try
        {
            if (package.Image != null)
            {
                // 将SixLabors.ImageSharp图像转换为BitmapSource
                CurrentImage = ConvertToBitmapSource(package.Image);
                Log.Debug("已更新当前图像");
            }
            else if (!string.IsNullOrEmpty(package.ImagePath) && File.Exists(package.ImagePath))
            {
                // 如果有图像路径但没有加载图像，尝试加载
                try
                {
                    using var image = Image.Load<Rgba32>(package.ImagePath);
                    CurrentImage = ConvertToBitmapSource(image);
                    Log.Debug("已从路径加载并更新当前图像: {ImagePath}", package.ImagePath);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "从路径加载图像失败: {ImagePath}", package.ImagePath);
                }
            }
            else
            {
                Log.Debug("包裹没有图像或图像路径");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新当前图像时出错");
        }
    }

    /// <summary>
    ///     将SixLabors.ImageSharp图像转换为BitmapSource
    /// </summary>
    /// <param name="image">ImageSharp图像</param>
    /// <returns>BitmapSource图像</returns>
    private static BitmapSource ConvertToBitmapSource(Image<Rgba32> image)
    {
        try
        {
            var width = image.Width;
            var height = image.Height;
            var stride = width * 4; // RGBA每像素4字节
            var pixelData = new byte[stride * height];

            // 复制像素数据
            image.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (var x = 0; x < width; x++)
                    {
                        var pixel = row[x];
                        var offset = y * stride + x * 4;
                        pixelData[offset] = pixel.B; // B
                        pixelData[offset + 1] = pixel.G; // G
                        pixelData[offset + 2] = pixel.R; // R
                        pixelData[offset + 3] = pixel.A; // A
                    }
                }
            });

            // 创建BitmapSource
            var bitmap = BitmapSource.Create(
                width,
                height,
                96, 96, // DPI
                PixelFormats.Bgra32,
                null,
                pixelData,
                stride);

            // 冻结位图以提高性能
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "转换图像格式时出错");
            return null!;
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
            try
            {
                // Stop timer
                _timer.Stop();

                // 取消事件订阅
                if (_cameraService != null) _cameraService.ConnectionChanged -= OnCameraConnectionChanged;

                // 释放所有订阅
                foreach (var subscription in _subscriptions) subscription.Dispose();
                _subscriptions.Clear();

                // 清理包裹历史
                foreach (var package in PackageHistory) package.Dispose();
                PackageHistory.Clear();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error occurred while releasing resources");
            }

        _disposed = true;
    }

    /// <summary>
    ///     处理相机连接状态变更
    /// </summary>
    /// <param name="isConnected">是否已连接</param>
    private void OnCameraConnectionChanged(bool isConnected)
    {
        try
        {
            // 添加调试日志，确认方法被调用
            Log.Debug("OnCameraConnectionChanged被调用: isConnected={IsConnected}", isConnected);

            // 使用Dispatcher确保在UI线程上更新
            Application.Current.Dispatcher.Invoke(() =>
            {
                var cameraStatus = DeviceStatuses.FirstOrDefault(s => s.Name == "Camera");
                if (cameraStatus == null)
                {
                    Log.Warning("未找到名为'Camera'的设备状态项");
                    return;
                }

                cameraStatus.Status = isConnected ? "Connected" : "Disconnected";
                cameraStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336"; // 绿色表示已连接，红色表示未连接

                Log.Information("Camera connection status updated to: {Status}",
                    cameraStatus.Status);
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error updating camera connection status");
        }
    }

    /// <summary>
    ///     更新设备状态
    /// </summary>
    /// <param name="deviceName">设备名称</param>
    /// <param name="isConnected">是否已连接</param>
    public void UpdateDeviceStatus(string deviceName, bool isConnected)
    {
        try
        {
            Log.Debug("UpdateDeviceStatus被调用: deviceName={DeviceName}, isConnected={IsConnected}",
                deviceName, isConnected);

            // 使用Dispatcher确保在UI线程上更新
            Application.Current.Dispatcher.Invoke(() =>
            {
                // 将中文设备名称转换为英文
                var englishName = deviceName switch
                {
                    "TCP相机" => "Camera",
                    _ => deviceName
                };

                var deviceStatus = DeviceStatuses.FirstOrDefault(s => s.Name == englishName);
                if (deviceStatus == null)
                {
                    Log.Warning("未找到名为'{DeviceName}'的设备状态项", englishName);
                    return;
                }

                deviceStatus.Status = isConnected ? "Connected" : "Disconnected";
                deviceStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336"; // 绿色表示已连接，红色表示未连接

                Log.Information("设备 {DeviceName} 连接状态已更新为: {Status}",
                    englishName, deviceStatus.Status);
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新设备状态时出错");
        }
    }

    #region Properties

    public DelegateCommand OpenSettingsCommand { get; }

    public DelegateCommand LogoutCommand { get; }

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

    public string CurrentEmployeeInfo
    {
        get => _currentEmployeeInfo;
        private set => SetProperty(ref _currentEmployeeInfo, value);
    }

    public ObservableCollection<PackageInfo> PackageHistory { get; } = [];
    public ObservableCollection<StatisticsItem> StatisticsItems { get; } = [];
    public ObservableCollection<DeviceStatus> DeviceStatuses { get; } = [];
    public ObservableCollection<PackageInfoItem> PackageInfoItems { get; } = [];

    #endregion
}