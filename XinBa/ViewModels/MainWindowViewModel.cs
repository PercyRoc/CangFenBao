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
using DeviceService.DataSourceDevices.Services;
using DeviceService.DataSourceDevices.Camera;
using MessageBox = HandyControl.Controls.MessageBox;
using MessageBoxImage = System.Windows.MessageBoxImage;
using System.Windows.Media.Imaging;
using DeviceService.DataSourceDevices.Weight;
using System.Windows.Media;
using XinBa.Services.Models;

namespace XinBa.ViewModels;

/// <summary>
///     主窗口视图模型
/// </summary>
public class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly IApiService _apiService;
    private readonly IDialogService _dialogService;
    private readonly ICameraService _cameraService;
    private readonly VolumeDataService _volumeDataService;
    private readonly SerialPortWeightService? _weightService;
    private readonly WildberriesApiService _wildberriesApiService;
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

    public MainWindowViewModel(
        IDialogService dialogService,
        PackageTransferService packageTransferService,
        IApiService apiService,
        ICameraService cameraService,
        VolumeDataService volumeDataService,
        WeightStartupService weightStartupService,
        WildberriesApiService wildberriesApiService)
    {
        _dialogService = dialogService;
        _apiService = apiService;
        _cameraService = cameraService;
        _volumeDataService = volumeDataService;
        _weightService = weightStartupService.GetWeightService();
        _wildberriesApiService = wildberriesApiService;

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
        _subscriptions.Add(packageTransferService.PackageStream
            .ObserveOn(TaskPoolScheduler.Default) // 或者 Scheduler.CurrentThread，根据需要调整
            .Subscribe(OnPackageReceived, ex => Log.Error(ex, "处理包裹流时发生错误")));
        Log.Information("已成功订阅 PackageTransferService 包裹流");

        // 订阅相机连接状态变化
        _cameraService.ConnectionChanged += OnCameraConnectionChanged;
        Log.Information("已成功订阅相机连接状态变化事件");

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
        Log.Information("已成功订阅相机图像流");

        // 订阅体积服务连接状态变化
        _volumeDataService.ConnectionChanged += OnVolumeConnectionChanged;
        Log.Information("已成功订阅体积服务连接状态变化事件");

        // --- 修改: 订阅重量服务连接状态变化 ---
        if (_weightService != null) // 确保服务实例不为空
        {
            _weightService.ConnectionChanged += OnWeightConnectionChanged;
            Log.Information("已成功订阅重量串口服务连接状态变化事件");
        }
        else
        {
            Log.Warning("无法订阅重量串口服务连接状态变化：服务实例为空。");
        }
        // --- 结束修改 ---

        // --- 修改: 初始化设备状态 (移除对 IsConnected 的直接访问) ---
        OnCameraConnectionChanged(null, _cameraService.IsConnected);
        OnVolumeConnectionChanged(_volumeDataService.IsConnected);
        if (_weightService == null) // 如果服务为空，显式设置为断开
        {
            Application.Current.Dispatcher.Invoke(() => UpdateDeviceStatus("Weight", false));
        }
        // --- 结束修改 ---
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

            // 添加重量设备状态 - Revert to property initializers
            DeviceStatuses.Add(new DeviceStatus
            {
                 Name = "Weight",
                 Status = "Disconnected",
                 Icon = "Scales24",
                 StatusColor = "#F44336"
            });

            // 添加体积相机状态 - Revert to property initializers
            DeviceStatuses.Add(new DeviceStatus
            {
                 Name = "Volume Camera",
                 Status = "Disconnected",
                 Icon = "ScanObject24",
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

            // 检查包裹是否已经有重量数据（大于0），如果没有且重量服务存在，则尝试查询
            if (package.Weight <= 0.0 && _weightService != null)
            {
                var weightInGrams = _weightService.FindNearestWeight(package.CreateTime);
                if (weightInGrams.HasValue)
                {
                    var weightInKg = weightInGrams.Value / 1000.0;
                    package.SetWeight(weightInKg);
                    Log.Information("为包裹 {Index} 找到并设置重量: {Weight} kg ({Grams} g)",
                        package.Index, weightInKg.ToString("F3"), weightInGrams.Value.ToString("F2"));
                }
                else
                {
                    Log.Warning("未找到包裹 {Index} 的重量数据", package.Index);
                }
            }

            // 检查包裹是否已经有体积数据，如果没有则尝试查询
            if (!package.Length.HasValue || !package.Width.HasValue || !package.Height.HasValue)
            {
                var volume = _volumeDataService.FindVolumeData(package);
                if (volume.HasValue)
                {
                    package.SetDimensions(volume.Value.Length, volume.Value.Width, volume.Value.Height);
                    Log.Information("为包裹 {Index} 找到并设置体积: L={Length}, W={Width}, H={Height}", 
                        package.Index, package.Length, package.Width, package.Height);
                }
                else
                {
                    Log.Warning("未找到包裹 {Index} 的体积数据", package.Index);
                }
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                CurrentBarcode = package.Barcode;
                UpdatePackageInfoItems(package);
                UpdateStatistics(package);
                AddToPackageHistory(package);
            });

            // 准备并发送新的Wildberries API请求
            if (package.Length.HasValue && package.Width.HasValue && package.Height.HasValue && package.Weight > 0.0)
            {
                var request = new TareAttributesRequest
                {
                    OfficeId = 300684, // 硬编码 Shelepanovo 仓库
                    TareSticker = package.Barcode, // 使用包裹条码
                    PlaceId = 943626653, // 硬编码 Shelepanovo 机器
                    SizeAMm = (long)package.Length.Value, // 长度，单位毫米
                    SizeBMm = (long)package.Width.Value, // 宽度，单位毫米
                    SizeCMm = (long)package.Height.Value, // 高度，单位毫米
                    VolumeMm = (int)(package.Length.Value * package.Width.Value * package.Height.Value), // 体积
                    WeightG = (int)(package.Weight * 1000) // 重量，转换为克
                };

                var (success, errorMessage) = await _wildberriesApiService.SendTareAttributesAsync(request);

                if (success)
                {
                    Log.Information("Wildberries TareAttributes提交成功: Barcode={Barcode}", package.Barcode);
                }
                else
                {
                    Log.Warning("Wildberries TareAttributes提交失败: Barcode={Barcode}, 错误: {ErrorMessage}", package.Barcode, errorMessage);
                }
            }
            else
            {
                Log.Warning("包裹缺少必要的尺寸或重量信息，无法提交到Wildberries API: Barcode={Barcode}, Length={Length}, Width={Width}, Height={Height}, Weight={Weight}", 
                    package.Barcode, package.Length, package.Width, package.Height, package.Weight);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹信息时出错: 条码={Barcode}", package.Barcode);
        }
        finally
        {
             // 即使不上传图片，也保持释放资源（如果PackageInfo中有其他可释放资源）
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

    /// <summary>
    /// 处理体积服务连接状态变化
    /// </summary>
    private void OnVolumeConnectionChanged(bool isConnected)
    {
        Log.Information("体积服务连接状态改变: IsConnected = {IsConnected}", isConnected);
        UpdateDeviceStatus("Volume Camera", isConnected);
    }

    // --- 修改: 处理重量服务连接状态变化 (调整签名) ---
    /// <summary>
    /// 处理重量服务连接状态变化
    /// </summary>
    /// <param name="deviceName">设备名 (来自事件)</param>
    /// <param name="isConnected">连接状态</param>
    private void OnWeightConnectionChanged(string deviceName, bool isConnected)
    {
        // deviceName 参数通常是 "Weight Scale" 或类似的，但我们更新状态时用的是 "Weight"
        Log.Information("重量服务连接状态改变 (来自 {DeviceName}): IsConnected = {IsConnected}", deviceName, isConnected);
        UpdateDeviceStatus("Weight", isConnected); // 使用固定的名称 "Weight" 更新UI
    }
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
                _cameraService.ConnectionChanged -= OnCameraConnectionChanged;
                Log.Information("已取消订阅相机连接状态变化事件");

                // 取消订阅体积服务事件
                _volumeDataService.ConnectionChanged -= OnVolumeConnectionChanged;
                Log.Information("已取消订阅体积服务连接状态变化事件");

                // --- 修改: 取消订阅重量服务事件 ---
                if (_weightService != null) // Check before unsubscribing
                {
                    _weightService.ConnectionChanged -= OnWeightConnectionChanged;
                    Log.Information("已取消订阅重量串口服务连接状态变化事件");
                }
                // --- 结束修改 ---

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

    public SystemStatus SystemStatus { get; private set; } = new();

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