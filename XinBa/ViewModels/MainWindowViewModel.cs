using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Threading;
using Common.Models.Package;
using Serilog;
using SharedUI.Models;
using XinBa.Services;
using MessageBox = HandyControl.Controls.MessageBox;
using MessageBoxImage = System.Windows.MessageBoxImage;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using DeviceService.DataSourceDevices.Camera.TCP;

namespace XinBa.ViewModels;

/// <summary>
///     主窗口视图模型
/// </summary>
public class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly IApiService _apiService;
    private readonly IDialogService _dialogService;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly DispatcherTimer _timer;
    private string _currentBarcode = string.Empty;
    private string _currentEmployeeInfo = string.Empty;
    private bool _disposed;
    private int _failedPackages;
    private DateTime _lastRateCalculationTime = DateTime.Now;
    private int _packagesInLastHour;
    private int _processingRate;
    private int _successPackages;
    private int _totalPackages;
    private BitmapSource? _currentImage;
    private TcpCameraService _tcpCameraService;

    public MainWindowViewModel(
        IDialogService dialogService,
        IApiService apiService, TcpCameraService tcpCameraService)
    {
        _dialogService = dialogService;
        _apiService = apiService;
        _tcpCameraService = tcpCameraService;

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

        InitializeDeviceStatuses();

        InitializeStatisticsItems();

        InitializePackageInfoItems();

        // 更新当前登录员工信息 - 初始调用
        _ = UpdateCurrentEmployeeInfo();

        // 新增：订阅 PackageTransferService 的包裹流
        _subscriptions.Add(_tcpCameraService.PackageStream
            .ObserveOn(TaskPoolScheduler.Default) // 或者 Scheduler.CurrentThread，根据需要调整
            .Subscribe(OnPackageReceived, ex => Log.Error(ex, "处理包裹流时发生错误")));
        Log.Information("已成功订阅 PackageTransferService 包裹流");

        // 订阅相机连接状态变化
        _tcpCameraService.ConnectionChanged += OnCameraConnectionChanged;
        Log.Information("已成功订阅相机连接状态变化事件");
        // --- 修改: 初始化设备状态 (移除对 IsConnected 的直接访问) ---
        OnCameraConnectionChanged(null, _tcpCameraService.IsConnected);
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
            var result = await Application.Current.Dispatcher.InvokeAsync(() =>
                MessageBox.Show(
                    "Are you sure you want to log out?",
                    "Logout Confirmation",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question));

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
                await Application.Current.Dispatcher.InvokeAsync(() =>
                    MessageBox.Show("登出失败，请稍后重试", "错误", MessageBoxButton.OK, MessageBoxImage.Error));
            }
            else
            {
                Log.Information("用户登出成功");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "执行登出过程中发生错误");
            await Application.Current.Dispatcher.InvokeAsync(() =>
                MessageBox.Show($"登出过程中发生错误: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error));
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
            Log.Debug("开始初始化设备状态列表");

            // 添加相机状态 - Revert to property initializers
            DeviceStatuses.Add(new DeviceStatus
            {
                 Name = "Camera",
                 Status = "Disconnected",
                 Icon = "Camera24",
                 StatusColor = "#F44336"
            });

            Log.Information("设备状态列表初始化完成, 总计 {Count} 个设备", DeviceStatuses.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化设备状态列表时发生错误");
        }
    }

    private void InitializeStatisticsItems()
    {
        StatisticsItems.Add(new StatisticsItem(
            "Total Packages",
            "0",
            "pcs",
            "Total number of processed packages",
            "BoxMultiple24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            "Success Count",
            "0",
            "pcs",
            "Number of successfully processed packages",
            "CheckmarkCircle24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            "Failure Count",
            "0",
            "pcs",
            "Number of failed packages",
            "ErrorCircle24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            "Processing Rate",
            "0",
            "pcs/hour",
            "Packages processed per hour",
            "ArrowTrendingLines24"
        ));
    }

    private void InitializePackageInfoItems()
    {
        PackageInfoItems.Add(new PackageInfoItem(
            "Weight",
            "0.00",
            "kg",
            "Package weight",
            "Scales24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "Dimensions",
            "0 × 0 × 0",
            "mm",
            "Length × Width × Height",
            "Ruler24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "Time",
            "--:--:--",
            "",
            "Processing time",
            "Timer24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "Status",
            "Waiting",
            "",
            "Processing status",
            "Alert24"
        ));
    }

    /// <summary>
    ///     更新当前登录员工信息
    /// </summary>
    internal Task UpdateCurrentEmployeeInfo()
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
            Log.Debug("接收到包裹信息: 条码={Barcode}", package.Barcode);

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
            });

            // 提交商品尺寸
            if (package is { Length: not null, Width: not null, Height: not null })
            {
                // 准备图片数据
                var photoData = new List<byte[]>();

                // 从本地文件夹加载图片 (带重试逻辑)
                var currentDate = DateTime.Now.ToString("yyyy-MM-dd");
                var imageDirectory = Path.Combine(@"E:\Image\Panorama", currentDate);
                const int maxAttempts = 10;
                const int delayBetweenAttemptsMs = 200;

                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    Log.Debug("图片查找尝试 {Attempt}/{MaxAttempts}，目录: {Directory}，包裹 Guid: {Guid}", attempt, maxAttempts, imageDirectory, package.Guid);
                    if (Directory.Exists(imageDirectory))
                    {
                        try
                        {
                            var imageFiles = Directory.GetFiles(imageDirectory, $"*{package.Guid}*");
                            Log.Debug("尝试 {Attempt}: 正在从目录 {Directory} 查找 Guid 为 {Guid} 的图片，找到 {Count} 个文件。", attempt, imageDirectory, package.Guid, imageFiles.Length);

                            foreach (var imageFile in imageFiles)
                            {
                                var extension = Path.GetExtension(imageFile).ToLowerInvariant();
                                if (extension is ".jpg" or ".jpeg" or ".png" or ".bmp")
                                {
                                    photoData.Add(File.ReadAllBytes(imageFile));
                                    Log.Information("尝试 {Attempt}: 已加载图片 {File} 到 photoData。", attempt, imageFile);
                                }
                                else
                                {
                                    Log.Debug("尝试 {Attempt}: 跳过非图片文件 {File}。", attempt, imageFile);
                                }
                            }

                            if (photoData.Count > 0)
                            {
                                Log.Information("尝试 {Attempt}: 成功找到并加载了 {Count} 张图片，Guid: {Guid}。跳出查找循环。", attempt, photoData.Count, package.Guid);
                                break; // 找到图片，退出循环
                            }
                            else
                            {
                                Log.Information("尝试 {Attempt}: 在目录 {Directory} 中未找到 Guid 为 {Guid} 的有效图片文件。", attempt, imageDirectory, package.Guid);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "尝试 {Attempt}: 从本地文件夹 {Directory} 加载图片时发生错误，Guid: {Guid}", attempt, imageDirectory, package.Guid);
                            photoData.Clear(); // 如果本次尝试出错，清空已部分加载的数据，准备下次尝试或最终提交空数据
                        }
                    }
                    else
                    {
                        Log.Warning("尝试 {Attempt}: 图片目录 {Directory} 不存在。", attempt, imageDirectory);
                    }

                    switch (photoData.Count)
                    {
                        // 如果未找到图片且未达到最大尝试次数，则等待后重试
                        case 0 when attempt < maxAttempts:
                            Log.Debug("尝试 {Attempt}: 未找到图片，将在 {Delay}ms 后重试...", attempt, delayBetweenAttemptsMs);
                            await Task.Delay(delayBetweenAttemptsMs);
                            break;
                        // 最后一次尝试仍未找到
                        case 0 when attempt == maxAttempts:
                            Log.Warning("已达到最大尝试次数 ({MaxAttempts})，仍未找到 Guid 为 {Guid} 的图片。", maxAttempts, package.Guid);
                            break;
                    }
                }

                // 提交尺寸信息
                var success = await _apiService.SubmitDimensionsAsync(
                    package.Barcode,
                    package.Height.Value.ToString("F2"),
                    package.Length.Value.ToString("F2"),
                    package.Width.Value.ToString("F2"),
                    package.Weight.ToString("F2"),
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
        finally
        {
             package.ReleaseImage();
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
            var weightItem = PackageInfoItems.FirstOrDefault(static p => p.Label == "Weight");

            if (weightItem != null)
            {
                weightItem.Value = $"{package.Weight:F2}";
            }

            // 更新尺寸
            var dimensionsItem = PackageInfoItems.FirstOrDefault(static p => p.Label == "Dimensions");
            if (dimensionsItem != null)
            {
                var length = package.Length ?? 0;
                var width = package.Width ?? 0;
                var height = package.Height ?? 0;
                dimensionsItem.Value = $"{length:F0} × {width:F0} × {height:F0}";
            }

            // 更新时间
            var timeItem = PackageInfoItems.FirstOrDefault(static p => p.Label == "Time");
            if (timeItem != null)
            {
                timeItem.Value = package.CreateTime.ToString("HH:mm:ss");
            }

            // 更新状态
            var statusItem = PackageInfoItems.FirstOrDefault(static p => p.Label == "Status");
            if (statusItem != null)
            {
                statusItem.Value = package.StatusDisplay;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新包裹信息项时出错");
        }
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
            var totalItem = StatisticsItems.FirstOrDefault(static s => s.Label == "Total Packages");
            if (totalItem != null)
            {
                totalItem.Value = _totalPackages.ToString();
            }

            // 更新成功/失败数量 (基于 PackageStatus.Success 和 PackageStatus.Error)
            var isSuccess = package.Status == PackageStatus.Success;
            var isFailure = package.Status == PackageStatus.Error || package.Status == PackageStatus.Failed ||
                            package.Status == PackageStatus.Timeout; // 假设这些都算失败

            if (isSuccess)
            {
                _successPackages++;
                var successItem = StatisticsItems.FirstOrDefault(static s => s.Label == "Success Count");
                if (successItem != null)
                {
                    successItem.Value = _successPackages.ToString();
                }
            }
            else if (isFailure) // 如果不是成功，且是明确的失败状态
            {
                _failedPackages++;
                var failureItem = StatisticsItems.FirstOrDefault(static s => s.Label == "Failure Count");
                if (failureItem != null)
                {
                    failureItem.Value = _failedPackages.ToString();
                }
            }
            // 注意：如果状态既不是 Success 也不是明确的 Failure (例如 Created)，则不计入成功或失败

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

            var rateItem = StatisticsItems.FirstOrDefault(static s => s.Label == "Processing Rate");
            if (rateItem != null)
            {
                rateItem.Value = _processingRate.ToString();
            }
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
            // 添加到历史记录的开头
            PackageHistory.Insert(0, package);

            // 限制历史记录数量
            const int maxHistoryItems = 1000;
            while (PackageHistory.Count > maxHistoryItems)
            {
               var removedPackage = PackageHistory[^1]; // Get the last item
               PackageHistory.RemoveAt(PackageHistory.Count - 1);
               removedPackage.Dispose(); // Dispose if not null
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "添加到包裹历史时出错");
        }
    }

    /// <summary>
    /// 处理相机连接状态变化
    /// </summary>
    private void OnCameraConnectionChanged(string? cameraId, bool isConnected)
    {
        Log.Information("相机连接状态改变: IsConnected = {IsConnected}, CameraId = {CameraId}", isConnected, cameraId ?? "N/A");
        UpdateDeviceStatus("Camera", isConnected);
    }

    // --- 修改: 处理重量服务连接状态变化 (调整签名) ---
    // --- 结束修改 ---

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
            try
            {
                // Stop timer
                _timer.Stop();

                // 取消订阅相机事件
                _tcpCameraService.ConnectionChanged -= OnCameraConnectionChanged;
                Log.Information("已取消订阅相机连接状态变化事件");

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
    ///     更新设备状态
    /// </summary>
    /// <param name="deviceName">设备名称</param>
    /// <param name="isConnected">是否已连接</param>
    private void UpdateDeviceStatus(string deviceName, bool isConnected)
    {
        try
        {
            Log.Debug("UpdateDeviceStatus被调用: deviceName={DeviceName}, isConnected={IsConnected}",
                deviceName, isConnected);

            // 使用Dispatcher确保在UI线程上更新
            Application.Current.Dispatcher.Invoke(() =>
            {
                var deviceStatus = DeviceStatuses.FirstOrDefault(s => s.Name == deviceName); // 直接使用设备名称

                if (deviceStatus == null)
                {
                    Log.Warning("未找到名为'{DeviceName}'的设备状态项", deviceName);
                    return;
                }

                deviceStatus.Status = isConnected ? "Connected" : "Disconnected";
                deviceStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336"; // 绿色表示已连接，红色表示未连接

                Log.Information("设备 {DeviceName} 连接状态已更新为: {Status}",
                    deviceName, deviceStatus.Status); // 使用 deviceName
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新设备状态时出错");
        }
    }

    /// <summary>
    /// 绘制单个比例尺及其标签
    /// </summary>
    private static void DrawRuler(DrawingContext dc, Pen pen, Brush textBrush, Typeface typeface, double fontSize, double pixelsPerDip,
                           Point startPoint, double length, double tickHeight, string label)
    {
        // 绘制主线
        dc.DrawLine(pen, startPoint, new Point(startPoint.X + length, startPoint.Y));

        // 绘制开始刻度
        dc.DrawLine(pen, new Point(startPoint.X, startPoint.Y - tickHeight / 2), new Point(startPoint.X, startPoint.Y + tickHeight / 2));

        // 绘制结束刻度
        dc.DrawLine(pen, new Point(startPoint.X + length, startPoint.Y - tickHeight / 2), new Point(startPoint.X + length, startPoint.Y + tickHeight / 2));

        // 绘制标签 (在比例尺右侧)
        var formattedLabel = new FormattedText(
            label,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            textBrush,
            pixelsPerDip
        );
        dc.DrawText(formattedLabel, new Point(startPoint.X + length + 5, startPoint.Y - formattedLabel.Height / 2)); // 垂直居中对齐
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

    private SystemStatus _systemStatus = new();
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