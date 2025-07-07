using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Common.Data;
using Common.Models.Package;
using Common.Models.Settings.ChuteRules;
using Common.Models.Settings.Sort.PendulumSort;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Scanner;
using DeviceService.DataSourceDevices.Services;
using FuzhouPolicyForce.WangDianTong;
using Serilog;
using SharedUI.Models;
using SortingServices.Pendulum;

namespace FuzhouPolicyForce.ViewModels;

internal class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly ICameraService _cameraService;
    private readonly IDialogService _dialogService;
    private readonly IPackageDataService _packageDataService;
    private readonly ISettingsService _settingsService;
    private readonly IPendulumSortService _sortService;
    private readonly List<IDisposable> _subscriptions = [];

    private readonly DispatcherTimer _timer;

    private readonly IWangDianTongApiService _wangDianTongApiService;

    private string _currentBarcode = string.Empty;

    private BitmapSource? _currentImage;

    private bool _disposed;
    private long _errorPackageCount;
    private long _successPackageCount;

    private SystemStatus _systemStatus = new();

    private long _totalPackageCount;

    public MainWindowViewModel(
        IDialogService dialogService,
        ICameraService cameraService,
        ISettingsService settingsService,
        IPendulumSortService sortService,
        PackageTransferService packageTransferService,
        ScannerStartupService scannerStartupService,
        IPackageDataService packageDataService,
        IWangDianTongApiService wangDianTongApiService)
    {
        _dialogService = dialogService;
        _cameraService = cameraService;
        _settingsService = settingsService;
        _sortService = sortService;
        _packageDataService = packageDataService;
        _wangDianTongApiService = wangDianTongApiService;
        scannerStartupService.GetScannerService();

        // 初始化命令
        OpenSettingsCommand = new DelegateCommand(ExecuteOpenSettings);
        OpenHistoryCommand = new DelegateCommand(ExecuteOpenHistory);

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

        // 订阅分拣设备连接事件
        _sortService.DeviceConnectionStatusChanged += OnDeviceConnectionStatusChanged;

        // 订阅相机连接状态事件
        _cameraService.ConnectionChanged += OnCameraConnectionChanged;

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

    public DelegateCommand OpenSettingsCommand { get; }

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

    public ObservableCollection<PackageInfo> PackageHistory { get; } = [];
    public ObservableCollection<StatisticsItem> StatisticsItems { get; } = [];
    public ObservableCollection<DeviceStatus> DeviceStatuses { get; } = [];
    public ObservableCollection<PackageInfoItem> PackageInfoItems { get; } = [];

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void ExecuteOpenSettings()
    {
        _dialogService.ShowDialog("SettingsDialog");
    }

    private void ExecuteOpenHistory()
    {
        _dialogService.ShowDialog("HistoryDialogView");
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
                Icon = "Camera",
                StatusColor = "#F44336" // 红色表示未连接
            });
            Log.Debug("已添加相机状态");

            // 获取分拣配置
            var sortConfig = _settingsService.LoadSettings<PendulumSortConfig>();
            Log.Debug("已加载分拣配置，光电数量: {Count}", sortConfig.SortingPhotoelectrics.Count);

            // 添加触发光电状态
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "触发光电",
                Status = "未连接",
                Icon = "RadioTower",
                StatusColor = "#F44336"
            });
            Log.Debug("已添加触发光电状态");

            // 添加分检光电状态
            foreach (var photoelectric in sortConfig.SortingPhotoelectrics)
            {
                DeviceStatuses.Add(new DeviceStatus
                {
                    Name = photoelectric.Name,
                    Status = "未连接",
                    Icon = "RadioTower",
                    StatusColor = "#F44336"
                });
                Log.Debug("已添加分拣光电状态: {Name}", photoelectric.Name);
            }

            Log.Information("设备状态列表初始化完成，共 {Count} 个设备", DeviceStatuses.Count);

            // 主动查询并更新摆轮分拣服务的设备连接状态
            var deviceStates = _sortService.GetAllDeviceConnectionStates();
            foreach (var (deviceName, isConnected) in deviceStates)
            {
                var deviceStatus = DeviceStatuses.FirstOrDefault(x => x.Name == deviceName);
                if (deviceStatus == null)
                {
                    Log.Warning("未找到设备状态项: {Name}", deviceName);
                    continue;
                }

                deviceStatus.Status = isConnected ? "已连接" : "已断开";
                deviceStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
                Log.Debug("已更新设备初始状态: {Name} -> {Status}", deviceName, deviceStatus.Status);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化设备状态列表时发生错误");
        }
    }

    private void InitializeStatisticsItems()
    {
        StatisticsItems.Add(new StatisticsItem(
            "总包裹数",
            "0",
            "个",
            "累计处理包裹总数",
            "CubeOutline24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            "异常数",
            "0",
            "个",
            "处理异常的包裹数量",
            "AlertOutline24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            "预测效率",
            "0",
            "个/小时",
            "预计每小时处理量",
            "TrendingUp24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            "平均处理时间",
            "0",
            "ms",
            "单个包裹平均处理时间",
            "TimerOutline24"
        ));
    }

    private void InitializePackageInfoItems()
    {
        PackageInfoItems.Add(new PackageInfoItem(
            "条码",
            "--",
            description: "包裹条码信息",
            icon: "Barcode24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "重量",
            "--",
            "kg",
            "包裹重量",
            "ScaleBalance24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "尺寸",
            "--",
            "cm",
            "长×宽×高",
            "RulerSquare24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "分拣口",
            "--",
            description: "目标分拣位置",
            icon: "ArrowSplitHorizontal24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "处理时间",
            "--",
            "ms",
            "系统处理耗时",
            "Timer24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "时间",
            "--:--:--",
            description: "包裹处理时间",
            icon: "Clock24"
        ));
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

    /// <summary>
    /// 检查旺店通API响应是否表示退款订单
    /// </summary>
    /// <param name="response">旺店通API响应</param>
    /// <returns>是否为退款订单</returns>
    private static bool IsRefundOrder(WeightPushResponse response)
    {
        // 检查响应消息中是否包含退款相关关键词
        if (!string.IsNullOrEmpty(response.Message))
        {
            var message = response.Message.ToLower();
            if (message.Contains("退款") || message.Contains("refund") || 
                message.Contains("退货") || message.Contains("return"))
            {
                return true;
            }
        }
        
        // 检查物流名称中是否包含退款相关关键词
        if (!string.IsNullOrEmpty(response.LogisticsName))
        {
            var logisticsName = response.LogisticsName.ToLower();
            if (logisticsName.Contains("退款") || logisticsName.Contains("refund") || 
                logisticsName.Contains("退货") || logisticsName.Contains("return"))
            {
                return true;
            }
        }
        
        return false;
    }

    /// <summary>
    /// 检查旺店通API响应是否表示重量不匹配
    /// </summary>
    /// <param name="response">旺店通API响应</param>
    /// <returns>是否为重量不匹配</returns>
    private static bool IsWeightMismatch(WeightPushResponse response)
    {
        if (string.IsNullOrEmpty(response.Message)) return false;
        
        var message = response.Message.ToLower();
        return message.Contains("重量") && (
            message.Contains("不匹配") || message.Contains("不符") || message.Contains("超限") ||
            message.Contains("weight") && (message.Contains("mismatch") || message.Contains("exceed") || 
                                         message.Contains("invalid") || message.Contains("error"))
        );
    }

    private async void OnPackageInfo(PackageInfo package)
    {
        Interlocked.Increment(ref _totalPackageCount);
        try
        {
            Application.Current.Dispatcher.Invoke(() => { CurrentBarcode = package.Barcode; });

            // 获取格口规则配置 - 始终需要，用于本地规则和异常口
            var chuteSettings = _settingsService.LoadSettings<ChuteSettings>();

            // 检查条码是否为空或noread - 这是最早的判断
            if (string.IsNullOrEmpty(package.Barcode) ||
                string.Equals(package.Barcode, "noread", StringComparison.OrdinalIgnoreCase))
            {
                package.SetChute(chuteSettings.NoReadChuteNumber);
                package.SetStatus(PackageStatus.Failed, "条码为空或无法识别");
                Log.Warning("包裹条码为空或noread，分配到NoRead格口 {NoReadChute}：{Barcode}", 
                    chuteSettings.NoReadChuteNumber, package.Barcode ?? "NULL");
                Interlocked.Increment(ref _errorPackageCount);

                // 直接处理包裹，跳过API调用和本地规则匹配
                _sortService.ProcessPackage(package);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        UpdatePackageInfoItems(package);
                        PackageHistory.Insert(0, package);
                        while (PackageHistory.Count > 1000)
                        {
                            var removedPackage = PackageHistory[^1];
                            PackageHistory.RemoveAt(PackageHistory.Count - 1);
                            removedPackage.Dispose();
                        }
                        UpdateStatistics();
                    }
                    catch (Exception ex) { Log.Error(ex, "更新UI时发生错误"); }
                });
                await SavePackageRecordAsync(package); // 保存记录
                package.ReleaseImage(); // 释放资源
                return; // 处理完毕，退出方法
            }

            // 条码有效，尝试调用旺店通API回传重量
            var apiSuccess = false;
            var apiMessage = "未调用API";

            try
            {
                using var cts = new CancellationTokenSource(300);
                var response = await _wangDianTongApiService.PushWeightAsync(
                    package.Barcode,
                    (decimal)package.Weight,
                    isCheckWeight: true,
                    isCheckTradeStatus: false,
                    packagerNo: "",
                    cancellationToken: cts.Token);

                // 只判断API回传是否成功，不使用API返回的格口号
                apiSuccess = response.IsSuccess;
                apiMessage = response.Message ?? (apiSuccess ? "API回传成功" : "API回传失败");

                if (!apiSuccess)
                {
                    // API 回传失败，先检查是否为退款订单
                    if (IsRefundOrder(response))
                    {
                        // 退款订单，使用退款格口
                        package.SetChute(chuteSettings.RefundChuteNumber);
                        package.SetStatus(PackageStatus.Success, $"退款订单: {apiMessage}");
                        Log.Information("包裹 {Barcode} 检测到退款订单(API失败)，分配到退款格口 {RefundChute}: {Message}", 
                            package.Barcode, chuteSettings.RefundChuteNumber, apiMessage);
                        Interlocked.Increment(ref _successPackageCount);
                    }
                    else if (IsWeightMismatch(response))
                    {
                        // 重量不匹配，使用重量不匹配格口
                        package.SetChute(chuteSettings.WeightMismatchChuteNumber);
                        package.SetStatus(PackageStatus.Error, $"重量不匹配: {apiMessage}");
                        Log.Warning("包裹 {Barcode} 重量不匹配，分配到重量不匹配格口 {WeightMismatchChute}: {Message}", 
                            package.Barcode, chuteSettings.WeightMismatchChuteNumber, apiMessage);
                        Interlocked.Increment(ref _errorPackageCount);
                    }
                    else
                    {
                        // 其他API失败，使用错误格口
                        package.SetChute(chuteSettings.ErrorChuteNumber);
                        package.SetStatus(PackageStatus.Error, $"API回传失败: {apiMessage}");
                        Log.Warning("包裹 {Barcode} 旺店通回传失败，分配到错误格口 {ErrorChute}: {Message}", 
                            package.Barcode, chuteSettings.ErrorChuteNumber, apiMessage);
                        Interlocked.Increment(ref _errorPackageCount);
                    }
                }
                else
                {
                    // API 回传成功，检查是否包含退款信息
                    if (IsRefundOrder(response))
                    {
                        // 退款订单，使用退款格口
                        package.SetChute(chuteSettings.RefundChuteNumber);
                        package.SetStatus(PackageStatus.Success, "退款订单已分配到退款格口");
                        Log.Information("包裹 {Barcode} 检测到退款订单，分配到退款格口 {RefundChute}", 
                            package.Barcode, chuteSettings.RefundChuteNumber);
                        Interlocked.Increment(ref _successPackageCount);
                    }
                    else
                    {
                        // 非退款订单，继续走本地规则判断
                        Log.Information("包裹 {Barcode} 旺店通回传成功 (将使用本地规则)", package.Barcode);
                        // API成功，此时不设置状态和格口，继续走本地规则判断
                    }
                }
            }
            catch (OperationCanceledException)
            {
                apiSuccess = false;
                apiMessage = "API调用超时 (超过300ms)";
                Log.Warning("旺店通API调用超时，分配到超时格口 {TimeoutChute}: {Barcode}", 
                    chuteSettings.TimeoutChuteNumber, package.Barcode);
                package.SetChute(chuteSettings.TimeoutChuteNumber);
                package.SetStatus(PackageStatus.Timeout, apiMessage);
                Interlocked.Increment(ref _errorPackageCount);
            }
            catch (Exception ex)
            {
                // API 调用本身发生异常，使用错误格口
                apiSuccess = false; // 标记API失败
                apiMessage = $"API调用异常: {ex.Message}";
                Log.Error(ex, "旺店通重量回传时发生异常，分配到错误格口 {ErrorChute}：{Barcode}", 
                    chuteSettings.ErrorChuteNumber, package.Barcode);
                package.SetChute(chuteSettings.ErrorChuteNumber);
                package.SetStatus(PackageStatus.Error, apiMessage);
                Interlocked.Increment(ref _errorPackageCount);
            }

            // 只有当API回传成功且未设置退款格口时，才执行本地格口规则匹配
            if (apiSuccess && package.ChuteNumber == 0)
            {
                try
                {
                    var matchedChute = chuteSettings.FindMatchingChute(package.Barcode);

                    if (matchedChute.HasValue)
                    {
                        // 本地规则匹配成功
                        package.SetChute(matchedChute.Value);
                        package.SetStatus(PackageStatus.Success, "本地规则匹配成功");
                        Log.Information("包裹 {Barcode} 本地规则匹配到格口 {Chute}", package.Barcode, matchedChute.Value);
                        Interlocked.Increment(ref _successPackageCount);
                    }
                    else
                    {
                        // 本地规则未匹配到，使用错误格口
                        package.SetChute(chuteSettings.ErrorChuteNumber);
                        package.SetStatus(PackageStatus.Failed, "本地规则未匹配到");
                        Log.Warning("包裹 {Barcode} 本地规则未匹配到任何规则，分配到错误格口 {ErrorChute}",
                            package.Barcode, chuteSettings.ErrorChuteNumber);
                        Interlocked.Increment(ref _errorPackageCount);
                    }
                }
                catch (Exception ex)
                {
                    // 本地规则匹配过程中发生异常，使用错误格口
                    Log.Error(ex, "执行本地格口规则时发生错误，分配到错误格口 {ErrorChute}：{Barcode}", 
                        chuteSettings.ErrorChuteNumber, package.Barcode);
                    package.SetChute(chuteSettings.ErrorChuteNumber);
                    package.SetStatus(PackageStatus.Error, $"本地规则异常: {ex.Message}");
                    Interlocked.Increment(ref _errorPackageCount);
                }
            }
            // 如果apiSuccess为false (API调用失败或返回失败状态，或调用异常)，则包裹状态和格口已经在上面的catch或!apiSuccess分支中设置为Error/异常口。

            // 统一处理包裹分拣、UI更新和数据保存
            _sortService.ProcessPackage(package);

            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    UpdatePackageInfoItems(package);
                    // 6. 更新统计信息和历史包裹列表
                    PackageHistory.Insert(0, package);
                    while (PackageHistory.Count > 1000) // 保持最近1000条记录
                    {
                        var removedPackage = PackageHistory[^1];
                        PackageHistory.RemoveAt(PackageHistory.Count - 1);
                        removedPackage.Dispose(); // 释放被移除的包裹
                    }

                    // 更新统计数据
                    UpdateStatistics();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "更新UI时发生错误");
                }
            });

            // 保存包裹记录到数据库
            await SavePackageRecordAsync(package);
        }
        catch (Exception ex)
        {
            // 顶层catch，捕获其他未处理的异常，使用错误格口
            Log.Fatal(ex, "处理包裹 {Barcode} 时发生未处理的异常", package.Barcode);
            package.SetStatus(PackageStatus.Error, $"未处理异常: {ex.Message}");
            // 这里的错误可能没有设置格口，为安全起见，再次确保设置错误格口
            try
            {
                var chuteSettings = _settingsService.LoadSettings<ChuteSettings>();
                package.SetChute(chuteSettings.ErrorChuteNumber);
                Log.Warning("包裹 {Barcode} 发生未处理异常，分配到错误格口 {ErrorChute}", 
                    package.Barcode, chuteSettings.ErrorChuteNumber);
            }
            catch
            { 
                Log.Error("无法加载格口设置，无法设置错误格口");
            }
            Interlocked.Increment(ref _errorPackageCount);

            // 尝试保存异常记录
            await SavePackageRecordAsync(package);

            // 通知UI更新以便显示错误信息
            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    UpdatePackageInfoItems(package);
                    PackageHistory.Insert(0, package);
                    while (PackageHistory.Count > 1000)
                    {
                        var removedPackage = PackageHistory[^1];
                        PackageHistory.RemoveAt(PackageHistory.Count - 1);
                        removedPackage.Dispose();
                    }
                    UpdateStatistics();
                }
                catch (Exception uiEx) { Log.Error(uiEx, "顶层异常处理后更新UI时发生错误"); }
            });
        }
        finally
        {
            package.ReleaseImage(); // 确保包裹图像资源被释放
        }
    }

    // Helper method to save package record, extracted to reduce duplication
    private async Task SavePackageRecordAsync(PackageInfo package)
    {
        try
        {
            await _packageDataService.AddPackageAsync(package);
            Log.Information("包裹记录已保存到数据库：{Barcode}", package.Barcode);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存包裹记录到数据库时发生错误：{Barcode}", package.Barcode);
            // 数据库保存失败是否算作异常？此处不重复增加_errorPackageCount，因为它反映的是业务处理异常。
        }
    }

    private void UpdatePackageInfoItems(PackageInfo package)
    {
        var barcodeItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "条码");
        if (barcodeItem != null)
        {
            barcodeItem.Value = package.Barcode;
            barcodeItem.Description = string.Equals(package.Barcode, "noread", StringComparison.OrdinalIgnoreCase)
                ? "条码识别失败"
                : "包裹条码信息";
        }

        var weightItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "重量");
        if (weightItem != null)
        {
            // package.Weight is already in kg (double)
            // Format double to string with 3 decimal places
            weightItem.Value = package.Weight.ToString("F3", CultureInfo.InvariantCulture);
            weightItem.Unit = "kg";
        }

        var sizeItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "尺寸");
        if (sizeItem != null)
        {
            sizeItem.Value = package.VolumeDisplay;
        }

        var segmentItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "段码");
        if (segmentItem != null)
        {
            segmentItem.Value = package.SegmentCode;
            segmentItem.Description = string.IsNullOrEmpty(package.SegmentCode) ? "等待获取..." : "三段码信息";
        }

        var chuteItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "分拣口");
        if (chuteItem != null)
        {
            chuteItem.Value = package.ChuteNumber.ToString();
            chuteItem.Description = package.ChuteNumber == 0 ? "等待分配..." : "目标分拣位置"; // 根据实际业务逻辑调整0是否表示待分配
            chuteItem.StatusColor = package.Status == PackageStatus.Success ? "#4CAF50" : "#F44336"; // 根据状态更新颜色
        }

        var timeItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "时间");
        if (timeItem != null)
        {
            timeItem.Value = package.CreateTime.ToString("HH:mm:ss");
            timeItem.Description = $"处理于 {package.CreateTime:yyyy-MM-dd}";
        }

        var processingTimeItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "处理时间");
        if (processingTimeItem == null) return;

        processingTimeItem.Value = $"{package.ProcessingTime:F0}";
        processingTimeItem.Description = $"耗时 {package.ProcessingTime:F0} 毫秒";
    }

    private void UpdateStatistics()
    {
        var totalItem = StatisticsItems.FirstOrDefault(static x => x.Label == "总包裹数");
        if (totalItem != null)
        {
            // 使用新的总数计数器
            totalItem.Value = _totalPackageCount.ToString();
            totalItem.Description = $"累计处理 {_totalPackageCount} 个包裹";
        }

        var errorItem = StatisticsItems.FirstOrDefault(static x => x.Label == "异常数");
        if (errorItem != null)
        {
            // 使用新的异常数计数器
            // var errorCount = PackageHistory.Count(static p => !string.IsNullOrEmpty(p.ErrorMessage)); // 移除旧逻辑
            var errorCount = _errorPackageCount; // 使用新计数器·
            errorItem.Value = errorCount.ToString();
            errorItem.Description = $"共有 {errorCount} 个异常包裹";
        }

        var efficiencyItem = StatisticsItems.FirstOrDefault(static x => x.Label == "预测效率");
        if (efficiencyItem != null)
        {
            // 获取最近30秒内的包裹
            var thirtySecondsAgo = DateTime.Now.AddSeconds(-30);
            var recentPackages = PackageHistory.Where(p => p.CreateTime > thirtySecondsAgo).ToList();

            if (recentPackages.Count > 0)
            {
                // 计算30秒内的平均处理速度（个/秒）
                var packagesPerSecond = recentPackages.Count / 30.0;
                // 转换为每小时处理量
                var hourlyRate = (int)(packagesPerSecond * 3600);
                efficiencyItem.Value = hourlyRate.ToString();
                efficiencyItem.Description = $"基于最近{recentPackages.Count}个包裹的处理速度";
            }
            else
            {
                efficiencyItem.Value = "0";
                efficiencyItem.Description = "暂无处理数据";
            }
        }

        var avgTimeItem = StatisticsItems.FirstOrDefault(static x => x.Label == "平均处理时间");
        if (avgTimeItem == null) return;

        {
            var recentPackages = PackageHistory.Take(100).ToList();
            if (recentPackages.Count != 0)
            {
                var avgTime = recentPackages.Average(static p => p.ProcessingTime);
                avgTimeItem.Value = avgTime.ToString("F0");
                avgTimeItem.Description = $"最近{recentPackages.Count}个包裹平均耗时";
            }
            else
            {
                avgTimeItem.Value = "0";
                avgTimeItem.Description = "暂无处理数据";
            }
        }
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
            try
            {
                // 停止定时器（UI线程操作）
                if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
                    Application.Current.Dispatcher.Invoke(() => _timer.Stop());
                else
                    _timer.Stop();

                // 取消订阅事件
                _sortService.DeviceConnectionStatusChanged -= OnDeviceConnectionStatusChanged;
                _cameraService.ConnectionChanged -= OnCameraConnectionChanged;
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
}