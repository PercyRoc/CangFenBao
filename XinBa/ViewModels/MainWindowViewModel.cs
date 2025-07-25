using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Common.Models.Package;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Services;
using DeviceService.DataSourceDevices.Weight;
using Serilog;
using SharedUI.Models;
using XinBa.Services;
using XinBa.Services.Models;

namespace XinBa.ViewModels;

/// <summary>
///     主窗口视图模型
/// </summary>
public partial class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly ICameraService _cameraService;
    private readonly IDialogService _dialogService;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly DispatcherTimer _timer;
    private readonly VolumeDataService _volumeDataService;
    private readonly SerialPortWeightService? _weightService;
    private readonly IQrCodeService _qrCodeService;
    private readonly ITareAttributesApiService _tareAttributesApiService;
    private string _currentBarcode = string.Empty;
    private string _manualBarcodeInput = string.Empty;
    private BitmapSource? _currentImage;
    private MeasurementResultViewModel? _currentMeasurementResult;
    private bool _disposed;
    private bool _isProcessingManualBarcode; // 标志位：是否正在处理手动输入的条码
    private int _failedPackages;
    private DateTime _lastRateCalculationTime = DateTime.Now;
    private int _packagesInLastHour;
    private int _processingRate;
    private int _successPackages;
    private int _totalPackages;
    private int _packageHistoryIndex = 1; // 用于PackageHistory的连续序号

    public MainWindowViewModel(
        IDialogService dialogService,
        PackageTransferService packageTransferService,
        ICameraService cameraService,
        VolumeDataService volumeDataService,
        WeightStartupService weightStartupService,
        IQrCodeService qrCodeService,
        ITareAttributesApiService tareAttributesApiService)
    {
        _dialogService = dialogService;
        _cameraService = cameraService;
        _volumeDataService = volumeDataService;
        _weightService = weightStartupService.GetWeightService();
        _qrCodeService = qrCodeService;
        _tareAttributesApiService = tareAttributesApiService;

        // Initialize commands
        OpenSettingsCommand = new DelegateCommand(ExecuteOpenSettings);
        ProcessManualBarcodeCommand = new DelegateCommand(ExecuteProcessManualBarcode, CanExecuteProcessManualBarcode);

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


    private void ExecuteOpenSettings()
    {
        _dialogService.ShowDialog("SettingsDialog");
    }

    /// <summary>
    /// 执行手动条码处理
    /// </summary>
    private async void ExecuteProcessManualBarcode()
    {
        try
        {
            var barcode = ManualBarcodeInput.Trim();
            if (string.IsNullOrEmpty(barcode))
            {
                Log.Warning("手动输入的条码为空");
                return;
            }

            // 设置标志位，暂停处理相机数据
            _isProcessingManualBarcode = true;
            Log.Information("开始处理手动输入的条码: {Barcode}，暂停处理相机数据", barcode);

            // 验证条码格式
            var barcodeMatch = MyRegex().Match(barcode);
            if (!barcodeMatch.Success)
            {
                Log.Warning("手动输入的条码 {Barcode} 格式不符合要求", barcode);
                return;
            }

            // 创建包裹信息
            var package = PackageInfo.Create();
            package.SetBarcode(barcode);
            package.SetStatus(PackageStatus.Processing, "Manual Input Processing");

            // 更新当前条码显示
            CurrentBarcode = barcode;

            // 并行获取重量和体积数据
            var tasks = new List<Task>();

            // 获取重量数据
            if (_weightService != null)
            {
                tasks.Add(Task.Run(() =>
                {
                    // 手动输入时，等待有效的重量数据
                    var weightInGrams = _weightService.WaitForValidWeight();
                    if (weightInGrams > 0)
                    {
                        var weightInKg = weightInGrams / 1000.0;
                        package.SetWeight(weightInKg);
                        Log.Information("为手动输入包裹设置重量: {Weight} kg ({Grams} g)",
                            weightInKg.ToString("F3"), weightInGrams.ToString("F2"));
                    }
                    else
                    {
                        Log.Warning("等待有效重量数据超时或失败");
                    }
                }));
            }

            // 获取体积数据
            tasks.Add(Task.Run(() =>
            {
                // 手动输入时，等待有效的体积数据
                var volumeData = _volumeDataService.WaitForValidVolumeData();
                if (volumeData != null)
                {
                    package.SetDimensions(volumeData.Value.Length, volumeData.Value.Width, volumeData.Value.Height);
                    Log.Information("为手动输入包裹设置体积: {Length}×{Width}×{Height} mm",
                        volumeData.Value.Length, volumeData.Value.Width, volumeData.Value.Height);
                }
                else
                {
                    Log.Warning("等待有效体积数据超时或失败");
                }
            }));

            // 等待所有任务完成
            await Task.WhenAll(tasks);

            // 设置包裹状态为成功
            package.SetStatus(PackageStatus.Success, "Manual Input Processing Completed");

            // 上传数据到API
            await UploadPackageDataAsync(package, "手动输入");

            // 记录原始序号并添加到历史记录
            var originalIndex = package.Index;
            AddToPackageHistory(package);

            Log.Information("手动输入包裹处理完成 - 条码: {Barcode}, 原始序号: {OriginalIndex}, 历史序号: {HistoryIndex}",
                package.Barcode, originalIndex, package.Index);

            // 更新UI
            Application.Current.Dispatcher.Invoke(() =>
            {
                UpdatePackageInfoItems(package);
                UpdateStatistics(package);
            });

            // 清空输入框
            ManualBarcodeInput = string.Empty;
            
            Log.Information("手动输入条码处理完成，恢复处理相机数据");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理手动输入条码时发生错误");
        }
        finally
        {
            // 无论成功还是失败，都要重置标志位，恢复相机数据处理
            _isProcessingManualBarcode = false;
        }
    }

    /// <summary>
    /// 判断是否可以执行手动条码处理
    /// </summary>
    /// <returns>如果输入不为空则返回true</returns>
    private bool CanExecuteProcessManualBarcode()
    {
        return !string.IsNullOrWhiteSpace(ManualBarcodeInput);
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
    ///     处理接收到的包裹信息
    ///     注意：由于条码过滤，PackageHistory中的序号可能不连续。
    ///     在AddToPackageHistory方法中会重新分配连续的序号。
    /// </summary>
    /// <param name="package">包裹信息</param>
    private async void OnPackageReceived(PackageInfo package)
    {
        try
        {
            // 如果正在处理手动输入的条码，则暂停处理相机数据
            if (_isProcessingManualBarcode)
            {
                Log.Debug("正在处理手动输入条码，跳过相机数据: 条码={Barcode}", package.Barcode);
                return;
            }

            var barcodeMatch = MyRegex().Match(package.Barcode);
            if (!barcodeMatch.Success)
            {
                Log.Warning("接收到的条码 {Barcode} 格式不符合要求，已忽略。原始序号: {Index}", package.Barcode, package.Index);
                return;
            }

            Log.Debug("接收到包裹信息: 条码={Barcode}", package.Barcode);

            var tasks = new List<Task>();

            // 如果需要，并行获取重量
            if (package.Weight <= 0.0 && _weightService != null)
            {
                tasks.Add(Task.Run(() =>
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
                }));
            }

            // 如果需要，并行获取体积
            if (!package.Length.HasValue || !package.Width.HasValue || !package.Height.HasValue)
            {
                tasks.Add(Task.Run(() =>
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
                }));
            }

            if (tasks.Count != 0)
            {
                await Task.WhenAll(tasks);
            }

            // 上传数据到API
            await UploadPackageDataAsync(package, "相机数据");

            Application.Current.Dispatcher.Invoke(() =>
            {
                var originalIndex = package.Index; // 记录原始序号
                CurrentBarcode = package.Barcode;
                UpdatePackageInfoItems(package);
                UpdateStatistics(package);
                AddToPackageHistory(package);
                
                Log.Debug("包裹已添加到历史记录: 原始序号={OriginalIndex}, 历史序号={HistoryIndex}, 条码={Barcode}", 
                    originalIndex, package.Index, package.Barcode);
            });
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
    /// 上传包裹数据到API
    /// </summary>
    /// <param name="package">包裹信息</param>
    /// <param name="dataSource">数据来源（相机数据或手动输入）</param>
    private async Task UploadPackageDataAsync(PackageInfo package, string dataSource)
    {
        try
        {
            // 检查API服务是否可用
            if (!_tareAttributesApiService.IsServiceAvailable())
            {
                Log.Warning("TareAttributesApiService 不可用，跳过数据上传 - 条码: {Barcode}, 数据来源: {DataSource}", 
                    package.Barcode, dataSource);
                return;
            }

            // 构建API请求
            var request = new TareAttributesRequest
            {
                OfficeId = 300864, // 默认仓库ID，可以从配置文件读取
                TareSticker = package.Barcode,
                PlaceId = 971319209, // 默认机器地点ID，可以从配置文件读取
                SizeAMm = (long)(package.Length ?? 0), // 已经是毫米单位
                SizeBMm = (long)(package.Width ?? 0),  // 已经是毫米单位
                SizeCMm = (long)(package.Height ?? 0), // 已经是毫米单位
                WeightG = (int)(package.Weight * 1000) // 转换为克
            };

            // 计算体积
            request.CalculateVolume();

            Log.Information("开始上传包裹数据到API - 条码: {Barcode}, 数据来源: {DataSource}, 尺寸: {Length}x{Width}x{Height}cm, 重量: {Weight}kg",
                package.Barcode, dataSource, package.Length, package.Width, package.Height, package.Weight);

            // 调用API
            var response = await _tareAttributesApiService.SubmitTareAttributesAsync(request);

            if (response.Success)
            {
                Log.Information("包裹数据上传成功 - 条码: {Barcode}, 数据来源: {DataSource}",
                    package.Barcode, dataSource);
            }
            else
            {
                Log.Error("包裹数据上传失败 - 条码: {Barcode}, 数据来源: {DataSource}, 错误: {Error}",
                    package.Barcode, dataSource, response.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "上传包裹数据时发生异常 - 条码: {Barcode}, 数据来源: {DataSource}",
                package.Barcode, dataSource);
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
            // 为包裹重新分配连续的历史序号，确保PackageHistory中的序号连续
            // 这解决了由于条码过滤导致的序号不连续问题
            package.Index = _packageHistoryIndex++;
            
            // 添加到历史记录的开头
            PackageHistory.Insert(0, package);

            // 限制历史记录数量
            const int maxHistoryItems = 1000;
            while (PackageHistory.Count > maxHistoryItems)
            {
                var removedPackage = PackageHistory[^1];
                PackageHistory.RemoveAt(PackageHistory.Count - 1);
                removedPackage.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "添加到包裹历史时出错");
        }
    }

    /// <summary>
    ///     处理相机连接状态变化
    /// </summary>
    private void OnCameraConnectionChanged(string? cameraId, bool isConnected)
    {
        Log.Information("相机连接状态改变: IsConnected = {IsConnected}, CameraId = {CameraId}", isConnected, cameraId ?? "N/A");
        UpdateDeviceStatus("Camera", isConnected);
    }

    /// <summary>
    ///     处理体积服务连接状态变化
    /// </summary>
    private void OnVolumeConnectionChanged(bool isConnected)
    {
        Log.Information("体积服务连接状态改变: IsConnected = {IsConnected}", isConnected);
        UpdateDeviceStatus("Volume Camera", isConnected);
    }

    // --- 修改: 处理重量服务连接状态变化 (调整签名) ---
    /// <summary>
    ///     处理重量服务连接状态变化
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

    protected void Dispose(bool disposing)
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
    public DelegateCommand ProcessManualBarcodeCommand { get; }

    public string ManualBarcodeInput
    {
        get => _manualBarcodeInput;
        set => SetProperty(ref _manualBarcodeInput, value);
    }

    public string CurrentBarcode
    {
        get => _currentBarcode;
        private set => SetProperty(ref _currentBarcode, value);
    }

    public bool HasCurrentBarcode => !string.IsNullOrEmpty(CurrentBarcode);

    public BitmapSource? CurrentImage
    {
        get => _currentImage;
        private set => SetProperty(ref _currentImage, value);
    }

    public MeasurementResultViewModel? CurrentMeasurementResult
    {
        get => _currentMeasurementResult;
        private set => SetProperty(ref _currentMeasurementResult, value);
    }

    public SystemStatus SystemStatus { get; private set; } = new();

    public ObservableCollection<PackageInfo> PackageHistory { get; } = [];
    public ObservableCollection<StatisticsItem> StatisticsItems { get; } = [];
    public ObservableCollection<DeviceStatus> DeviceStatuses { get; } = [];
    public ObservableCollection<PackageInfoItem> PackageInfoItems { get; } = [];

    [GeneratedRegex(@"^\$1:1:\d+:\d+$")]
    private static partial Regex MyRegex();

    #endregion
}