using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BenFly.Services;
using Common.Data;
using Common.Models.Package;
using Common.Models.Settings.ChuteRules;
using Common.Models.Settings.Sort.PendulumSort;
using Common.Services.Audio;
using Common.Services.Settings;
using Common.Services.Validation;
using DeviceService.DataSourceDevices.Belt;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Scanner;
using DeviceService.DataSourceDevices.Services;
using Serilog;
using SharedUI.Models;
using SortingServices.Pendulum;

namespace BenFly.ViewModels.Windows;

internal class MainWindowViewModel : BindableBase, IDisposable
{
    // 用于为合并后的包裹生成新的、线程安全的序号
    private static int _nextMergedPackageIndex;
    private readonly IAudioService _audioService;
    private readonly IDisposable? _barcodeSubscription; // Subscription for the barcode stream
    private readonly IBarcodeValidationService _barcodeValidationService;

    private readonly BeltSerialService _beltSerialService;

    private readonly BenNiaoPackageService _benNiaoService;
    private readonly ICameraService _cameraService;
    private readonly IDialogService _dialogService;
    private readonly IPackageDataService _packageDataService;
    private readonly BenNiaoPreReportService _preReportService;
    private readonly ISettingsService _settingsService;
    private readonly IPendulumSortService _sortService;
    private readonly List<IDisposable> _subscriptions = [];

    private readonly DispatcherTimer _timer;
    private long _chuteAllocationErrorCount;

    private string _currentBarcode = string.Empty;

    private BitmapSource? _currentImage;

    private bool _disposed;
    private long _noReadPackageCount;

    private SystemStatus _systemStatus = new();

    // 统计计数器 - 使用线程安全的方式
    private long _totalPackageCount;
    private long _uploadErrorCount;

    public MainWindowViewModel(
        IDialogService dialogService,
        ICameraService cameraService,
        ISettingsService settingsService,
        IPendulumSortService sortService,
        PackageTransferService packageTransferService,
        BenNiaoPackageService benNiaoService,
        BenNiaoPreReportService preReportService,
        ScannerStartupService scannerStartupService,
        IAudioService audioService,
        BeltSerialService beltSerialService,
        IPackageDataService packageDataService,
        IBarcodeValidationService barcodeValidationService)
    {
        _dialogService = dialogService;
        _cameraService = cameraService;
        _settingsService = settingsService;
        _sortService = sortService;
        _benNiaoService = benNiaoService;
        _preReportService = preReportService;
        _packageDataService = packageDataService;
        var scannerService = scannerStartupService.GetScannerService();
        _audioService = audioService;
        _beltSerialService = beltSerialService;
        _barcodeValidationService = barcodeValidationService;
        // 订阅扫码枪事件 - REMOVED
        // _scannerService.BarcodeScanned += OnBarcodeScannerScanned;

        // 订阅扫码流
        _barcodeSubscription = scannerService.BarcodeStream
            .ObserveOn(Scheduler.CurrentThread) // Ensure UI updates are on the correct thread
            .Subscribe(
                HandleBarcodeFromStream, // Call the handler method
                ex => Log.Error(ex, "扫码流发生错误") // Handle errors in the stream
            );

        // 订阅皮带连接状态事件
        _beltSerialService.ConnectionStatusChanged += OnBeltConnectionStatusChanged;

        // 初始化命令   
        OpenSettingsCommand = new DelegateCommand(ExecuteOpenSettings);
        OpenHistoryCommand = new DelegateCommand(ExecuteOpenHistory);
        OpenCalibrationCommand = new DelegateCommand(ExecuteOpenCalibration);

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
            .Buffer(TimeSpan.FromMilliseconds(200))
            .Where(buffer => buffer.Any()) // 确保buffer不为空
            .Select(MergePackageInfos) // 使用合并函数
            .ObserveOn(Scheduler.CurrentThread) // 切换回UI线程
            .Subscribe(OnPackageInfo));

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

        // 记录新功能启用
        Log.Information("已启用新的分类异常统计功能：NoRead异常、格口分配异常、上传异常分别统计");
    }

    public DelegateCommand OpenSettingsCommand { get; }
    public DelegateCommand OpenHistoryCommand { get; }
    public DelegateCommand OpenCalibrationCommand { get; }

    public string CurrentBarcode
    {
        get => _currentBarcode;
        set => SetProperty(ref _currentBarcode, value);
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
        _dialogService.ShowDialog("HistoryDialog");
    }

    private void ExecuteOpenCalibration()
    {
        try
        {
            Log.Information("用户打开分拣时间标定对话框");
            _dialogService.ShowDialog("CalibrationDialog");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "打开标定对话框时发生错误");
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
            Log.Debug("已添加相机状态");

            // 添加皮带状态
            var beltSettings = _settingsService.LoadSettings<BeltSerialParams>(); // Load settings
            var beltStatus = new DeviceStatus
            {
                Name = "皮带",
                Icon = "AlignStartVertical20"
            };

            if (!beltSettings.IsEnabled)
            {
                beltStatus.Status = "已禁用";
                beltStatus.StatusColor = "#9E9E9E"; // Gray color for disabled
                Log.Debug("皮带串口已禁用，状态设置为 '已禁用'");
            }
            else
            {
                // If enabled, check the initial connection status
                beltStatus.Status = _beltSerialService.IsOpen ? "已连接" : "未连接"; // Or "已断开"? Keep consistent
                beltStatus.StatusColor = _beltSerialService.IsOpen ? "#4CAF50" : "#F44336"; // Green/Red
                Log.Debug("皮带串口已启用，初始状态: {Status}", beltStatus.Status);
            }

            DeviceStatuses.Add(beltStatus); // Add the configured status object
            Log.Debug("已添加皮带状态");

            // 获取分拣配置
            var sortConfig = _settingsService.LoadSettings<PendulumSortConfig>();
            Log.Debug("已加载分拣配置，光电数量: {Count}", sortConfig.SortingPhotoelectrics.Count);

            // 添加触发光电状态
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "触发光电",
                Status = "未连接",
                Icon = "LightbulbPerson24",
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
                    Icon = "LightbulbPerson24",
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

            // 更新皮带初始状态 - 这行不再需要，已在添加时处理
            // UpdateBeltStatus(_beltSerialService.IsOpen);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化设备状态列表时发生错误");
        }
    }

    private void OnBeltConnectionStatusChanged(bool isConnected)
    {
        UpdateBeltStatus(isConnected);
    }

    private void UpdateBeltStatus(bool isConnected)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var beltStatus = DeviceStatuses.FirstOrDefault(static x => x.Name == "皮带");
            if (beltStatus == null) return;

            var beltSettings = _settingsService.LoadSettings<BeltSerialParams>();

            if (!beltSettings.IsEnabled)
            {
                beltStatus.Status = "已禁用";
                beltStatus.StatusColor = "#9E9E9E";
                Log.Debug("皮带串口已禁用 (在状态更新时检测到)");
            }
            else
            {
                beltStatus.Status = isConnected ? "已连接" : "已断开";
                beltStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336"; // Green/Red
                Log.Debug("皮带串口已启用，状态更新为: {Status}", beltStatus.Status);
            }
        });
    }

    private void InitializeStatisticsItems()
    {
        StatisticsItems.Add(new StatisticsItem(
            "总包裹数",
            "0",
            "个",
            "累计处理包裹总数",
            "CubeMultiple24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            "NoRead异常",
            "0",
            "个",
            "无法识别条码的包裹数量",
            "ScanCamera24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            "格口分配异常",
            "0",
            "个",
            "已上传但格口分配失败的包裹",
            "LocationOff24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            "上传异常",
            "0",
            "个",
            "无法上传到笨鸟系统的包裹",
            "CloudOff24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            "预测效率",
            "0",
            "个/小时",
            "预计每小时处理量",
            "ArrowTrending24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            "平均处理时间",
            "0",
            "ms",
            "单个包裹平均处理时间",
            "Timer24"
        ));
    }

    private void InitializePackageInfoItems()
    {
        PackageInfoItems.Add(new PackageInfoItem(
            "重量",
            "--",
            "kg",
            "包裹重量",
            "Scales24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "尺寸",
            "--",
            "cm",
            "长×宽×高",
            "Ruler24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "段码",
            "--",
            description: "三段码信息",
            icon: "BarcodeScanner24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "分拣口",
            "--",
            description: "目标分拣位置",
            icon: "ArrowCircleDown24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "处理时间",
            "--",
            "ms",
            "系统处理耗时",
            "Timer24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "当前时间",
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

    // Handles barcodes received from the IObservable stream
    private void HandleBarcodeFromStream(string barcode)
    {
        Log.Information("收到巴枪扫码：{Barcode}", barcode);

        // 更新显示
        Application.Current.Dispatcher.Invoke(() =>
        {
            // 直接更新条码输入框显示
            CurrentBarcode = barcode;

            // 更新包裹信息项显示
            var barcodeItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "条码");
            if (barcodeItem == null) return;
            barcodeItem.Value = barcode;
            barcodeItem.Description = "请按回车键确认";
        });
    }

    /// <summary>
    ///     处理用户手动输入或巴枪扫码的条码（按回车键触发）
    /// </summary>
    public void OnBarcodeInput()
    {
        var barcode = CurrentBarcode;
        // 移除提示文本
        barcode = barcode.Replace(" (请按回车键确认...)", "").Replace(" (请按回车键继续...)", "");

        if (string.IsNullOrWhiteSpace(barcode)) return;
        Log.Information("用户通过输入框确认条码：{Barcode}", barcode);
    }

    private async void OnPackageInfo(PackageInfo package)
    {
        try
        {
            await OnPackageInfoAsync(package);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "处理包裹信息时发生未处理的异常：Barcode: {Barcode}, Index: {Index}", package.Barcode, package.Index);
            try
            {
                // 即使出错也尝试更新状态并保存基本信息
                package.SetStatus(PackageStatus.Error, $"系统严重异常: {ex.Message}");
                await Application.Current.Dispatcher.InvokeAsync(() => UpdatePackageInfoItems(package));
            }
            catch (Exception updateEx)
            {
                Log.Fatal(updateEx, "更新包裹状态时也发生异常");
            }
        }
    }

    /// <summary>
    ///     处理包裹信息的核心逻辑
    /// </summary>
    private async Task OnPackageInfoAsync(PackageInfo package)
    {
        try
        {
            // 在方法开始时立即更新CurrentBarcode
            await Application.Current.Dispatcher.InvokeAsync(() => { CurrentBarcode = package.Barcode; });

            // 如果不是noread，播放成功音效
            if (!string.Equals(package.Barcode, "noread", StringComparison.OrdinalIgnoreCase))
            {
                _ = _audioService.PlayPresetAsync(AudioType.Success);
            }

            Log.Information("收到包裹信息：Barcode: {Barcode}, Index: {Index}", package.Barcode, package.Index);

            // 提前加载格口规则以获取异常口
            var segmentCodeRules = _settingsService.LoadSettings<SegmentCodeRules>();
            var exceptionChute = segmentCodeRules.ExceptionChute;

            // 暂存原始图像，处理完成后清空
            var originalImage = package.Image;

            // 检查条码是否为 noread 或空
            var isNoReadOrEmpty = string.IsNullOrWhiteSpace(package.Barcode) ||
                                  string.Equals(package.Barcode, "noread", StringComparison.OrdinalIgnoreCase);

            // 单号校验结果
            BarcodeValidationResult? validationResult = null;
            var isBarcodeInvalid = false;

            // 对非空且非noread的条码进行校验
            if (!isNoReadOrEmpty)
            {
                validationResult = _barcodeValidationService.ValidateBarcode(package.Barcode);
                isBarcodeInvalid = !validationResult.IsValid;

                if (isBarcodeInvalid)
                {
                    Log.Warning("单号校验失败: {Barcode}, 错误: {Error}", package.Barcode, validationResult.ErrorMessage);
                    // 播放错误音效
                    _ = _audioService.PlayPresetAsync(AudioType.SystemError);
                }
                else
                {
                    Log.Information("单号校验通过: {Barcode}, 类型: {Type}", package.Barcode, validationResult.BarcodeType);
                }
            }
            // 检查数据是否有效 (重量 > 0 且 尺寸 > 0)
            var isInvalidData = package.Weight <= 0 ||
                                !package.Length.HasValue || package.Length.Value <= 0 ||
                                !package.Width.HasValue || package.Width.Value <= 0 ||
                                !package.Height.HasValue || package.Height.Value <= 0;


            // 如果条码无效 或 单号校验失败 或 数据无效
            if (isNoReadOrEmpty || isBarcodeInvalid || isInvalidData)
            {
                // 播放错误音效 (对于 NoRead，如果需要不同音效，可以调整)
                _ = _audioService.PlayPresetAsync(AudioType.SystemError);

                // 分配到配置的异常口
                package.SetChute(exceptionChute);

                // 确定异常原因
                string reason;
                if (isNoReadOrEmpty)
                    reason = "NoRead/Empty Barcode";
                else if (isBarcodeInvalid)
                    reason = $"Invalid Barcode Format: {validationResult?.ErrorMessage}";
                else
                    reason = "Invalid Weight/Volume";

                Log.Information("包裹 Barcode: {Barcode}, Index: {Index} 因 '{Reason}',预分配到配置的异常口 {ExceptionChute}",
                    package.Barcode, package.Index, reason, exceptionChute);

                // 根据不同错误类型设置状态和消息
                if (isBarcodeInvalid)
                {
                    var barcodeErrorMsg = $"单号校验失败: {validationResult?.ErrorMessage}";
                    package.SetStatus(PackageStatus.Error, barcodeErrorMsg);
                    Log.Information("包裹单号校验失败...状态设为Error, ErrorMessage: '{ErrorMessage}'. 将标记为 uploadAsNoRead。",
                        package.Barcode);
                }
                else if (isInvalidData)
                {
                    const string invalidDataErrorMsg = "包裹重量或体积数据无效";
                    package.SetStatus(PackageStatus.Error, invalidDataErrorMsg);
                    Log.Information("包裹数据无效...状态设为Error, ErrorMessage: '{ErrorMessage}'. 将标记为 uploadAsNoRead。",
                        package.Barcode);
                }
                else // isNoReadOrEmpty
                {
                    // 对于 NoRead 包裹，不管之前的状态是什么，都应该设置为 NoRead 状态
                    // 这样才能正确统计 NoRead 异常计数
                    package.SetStatus(PackageStatus.NoRead, "未能识别包裹条码(NoRead)");
                    Log.Information("包裹为 NoRead/Empty...状态已设置为 NoRead。Barcode: {Barcode}",
                        package.Barcode);
                }
            }
            else // 条码和数据有效，开始与笨鸟系统交互
            {
                var benNiaoInteractionSuccess = true;
                var benNiaoErrorMessage = string.Empty;
                var uploadTime = DateTime.MinValue;

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var cancellationToken = cts.Token;

                try
                {
                    // 1. 获取段码
                    var preReportData = _preReportService.GetPreReportData();
                    var preReportItem = preReportData?.FirstOrDefault(x => x.WaybillNum == package.Barcode);

                    if (preReportItem != null && !string.IsNullOrWhiteSpace(preReportItem.SegmentCode))
                    {
                        Log.Information("在预报数据中找到包裹的三段码: {SegmentCode}。Barcode: {Barcode}, Index: {Index}",
                            preReportItem.SegmentCode, package.Barcode, package.Index);
                        package.SetSegmentCode(preReportItem.SegmentCode);
                    }
                    else
                    {
                        Log.Information("预报数据未找到包裹，尝试实时查询... Barcode: {Barcode}, Index: {Index}", package.Barcode, package.Index);
                        var (segmentCode, segmentError) = await _benNiaoService.GetRealTimeSegmentCodeAsync(package.Barcode, cancellationToken);
                        if (!string.IsNullOrWhiteSpace(segmentCode))
                        {
                            Log.Information("通过实时查询获取到包裹的三段码: {SegmentCode}。Barcode: {Barcode}, Index: {Index}",
                                segmentCode, package.Barcode, package.Index);
                            package.SetSegmentCode(segmentCode);
                        }
                        else
                        {
                            benNiaoInteractionSuccess = false;
                            benNiaoErrorMessage = string.IsNullOrWhiteSpace(segmentError) ? "无法获取三段码(未知原因)" : segmentError;
                            package.SetStatus(PackageStatus.Error, benNiaoErrorMessage);
                            Log.Warning("无法获取包裹的三段码: {Error}。Barcode: {Barcode}, Index: {Index}", benNiaoErrorMessage, package.Barcode, package.Index);
                        }
                    }

                    // 2. 上传包裹数据 (仅当获取段码成功时)
                    if (benNiaoInteractionSuccess)
                    {
                        Log.Information("调用笨鸟 UploadPackageDataAsync for Barcode: {Barcode}, Index: {Index}", package.Barcode, package.Index);
                        var (dataSuccess, time, dataErrorMessage) = await _benNiaoService.UploadPackageDataAsync(package, cancellationToken);
                        if (!dataSuccess)
                        {
                            benNiaoInteractionSuccess = false;
                            benNiaoErrorMessage = string.IsNullOrEmpty(dataErrorMessage) ? "数据上传失败(未知原因)" : dataErrorMessage;
                            package.SetStatus(PackageStatus.Error, benNiaoErrorMessage);
                            Log.Warning("笨鸟数据上传失败: Barcode: {Barcode}, Index: {Index}, Error: {Error}", package.Barcode, package.Index, benNiaoErrorMessage);
                        }
                        else
                        {
                            uploadTime = time;
                            Log.Information("笨鸟数据上传成功: Barcode: {Barcode}, Index: {Index}", package.Barcode, package.Index);
                            package.SetStatus(PackageStatus.Success); // 初始状态设为成功
                            package.SetBenNiaoUploadStatus(true); // 标记为已上传到笨鸟系统
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    benNiaoInteractionSuccess = false;
                    benNiaoErrorMessage = "API请求超时(2秒)";
                    package.SetStatus(PackageStatus.Error, benNiaoErrorMessage);
                    Log.Error("与笨鸟系统交互时发生超时。Barcode: {Barcode}, Index: {Index}", package.Barcode, package.Index);
                }
                catch (Exception ex)
                {
                    benNiaoInteractionSuccess = false;
                    benNiaoErrorMessage = $"API交互异常: {ex.Message}";
                    package.SetStatus(PackageStatus.Error, benNiaoErrorMessage);
                    Log.Error(ex, "与笨鸟系统交互时发生异常。Barcode: {Barcode}, Index: {Index}", package.Barcode, package.Index);
                }

                // 3. 根据交互结果分配格口
                if (benNiaoInteractionSuccess)
                {
                    try
                    {
                        var chute = segmentCodeRules.GetChuteBySpaceSeparatedSegments(package.SegmentCode);
                        package.SetChute(chute);
                        Log.Information("包裹分配到格口 {Chute}，段码：{SegmentCode}。Barcode: {Barcode}, Index: {Index}",
                            chute, package.SegmentCode, package.Barcode, package.Index);

                        // 4. 启动图片上传 (仅当数据上传成功且有图片时)
                        if (originalImage != null)
                        {
                            StartBackgroundImageUpload(package, originalImage, uploadTime);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "获取格口号时发生错误：SegmentCode: {SegmentCode}。Barcode: {Barcode}, Index: {Index}", package.SegmentCode, package.Barcode, package.Index);
                        package.SetStatus(PackageStatus.Error, $"格口分配错误: {ex.Message}");
                        package.SetChute(exceptionChute);
                        Log.Information("包裹因获取格口号失败，分配到异常格口 {ExceptionChute}。Barcode: {Barcode}, Index: {Index}", exceptionChute, package.Barcode, package.Index);
                    }
                }
                else
                {
                    // 如果笨鸟交互失败，分配到异常口
                    package.SetChute(exceptionChute);
                    Log.Information("包裹因处理失败 ({FailureReason})，分配到异常格口 {ExceptionChute}。Barcode: {Barcode}, Index: {Index}",
                        benNiaoErrorMessage, exceptionChute, package.Barcode, package.Index);
                }
            }

            // ****** 格口已确定，现在提交到分拣服务 ******
            try
            {
                _sortService.ProcessPackage(package);
                Log.Information("包裹已提交到分拣服务: Barcode: {Barcode}, Index: {Index}, Chute: {Chute}",
                    package.Barcode, package.Index, package.ChuteNumber);
            }
            catch (Exception ex)
            {
                var errorMsg = $"提交到分拣服务异常: {ex.Message}";
                Log.Error(ex, "提交包裹到分拣服务时发生错误。Barcode: {Barcode}, Index: {Index}", package.Barcode, package.Index);
                // 此时格口已经定了，但需要更新状态
                package.SetStatus(PackageStatus.Error, errorMsg);
            }

            // ****** 更新UI和数据库 ******

            // 更新统计计数器（线程安全）
            Interlocked.Increment(ref _totalPackageCount);

            // 分别统计不同类型的异常
            if (package.Status == PackageStatus.NoRead)
            {
                Interlocked.Increment(ref _noReadPackageCount);
            }
            else if (package.Status == PackageStatus.Error)
            {
                if (!package.IsUploadedToBenNiao)
                {
                    // 上传异常：无法上传到笨鸟系统
                    Interlocked.Increment(ref _uploadErrorCount);
                }
                else
                {
                    // 格口分配异常：已上传但后续处理失败
                    Interlocked.Increment(ref _chuteAllocationErrorCount);
                }
            }

            // 更新UI显示
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    // 更新条码显示
                    CurrentBarcode = package.Barcode;

                    // 更新包裹信息项显示
                    UpdatePackageInfoItems(package);

                    // 添加到历史记录
                    PackageHistory.Insert(0, package);
                    while (PackageHistory.Count > 1000)
                        PackageHistory.RemoveAt(PackageHistory.Count - 1);

                    UpdateStatistics();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "最终更新UI时发生错误。Barcode: {Barcode}, Index: {Index}", package.Barcode, package.Index);
                }
            });

            // 记录包裹最终状态用于问题排查
            Log.Information("包裹处理完成 - Barcode: {Barcode}, Index: {Index}, Status: {Status}, IsUploadedToBenNiao: {IsUploadedToBenNiao}, Chute: {Chute}",
                package.Barcode, package.Index, package.Status, package.IsUploadedToBenNiao, package.ChuteNumber);

            // 异步保存数据到数据库
            try
            {
                Log.Debug("准备将包裹 {Barcode} (Index: {Index}) 信息异步保存到数据库...", package.Barcode, package.Index);
                await _packageDataService.AddPackageAsync(package);
                Log.Information("包裹 {Barcode} (Index: {Index}) 信息已成功异步保存到数据库。", package.Barcode, package.Index);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "异步保存包裹 {Barcode} (Index: {Index}) 到数据库时发生错误", package.Barcode, package.Index);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹信息时发生严重错误：Barcode: {Barcode}, Index: {Index}", package.Barcode, package.Index);
            package.SetStatus(PackageStatus.Error, $"系统异常: {ex.Message}");
            // 尝试更新UI以反映错误
            await Application.Current.Dispatcher.InvokeAsync(() => UpdatePackageInfoItems(package));
        }
        finally
        {
            // 清理图像资源
            if (package.Image != null)
            {
                package.Image = null;
                Log.Debug("已清除包裹的图像引用。Barcode: {Barcode}, Index: {Index}", package.Barcode, package.Index);
            }
        }
    }

    /// <summary>
    ///     启动后台图片上传任务
    /// </summary>
    private void StartBackgroundImageUpload(PackageInfo package, BitmapSource originalImage, DateTime uploadTime)
    {
        Log.Information("准备启动后台图片上传 for Barcode: {Barcode}, Index: {Index}", package.Barcode, package.Index);
        try
        {
            var tempImagePath = _benNiaoService.SaveImageToTempFileAsync(originalImage, package.Barcode, uploadTime, package);
            if (!string.IsNullOrWhiteSpace(tempImagePath))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var (imageUploadSuccess, imageUploadError) = await _benNiaoService.UploadImageAsync(package.Barcode, uploadTime, tempImagePath);
                        if (!imageUploadSuccess)
                        {
                            Log.Warning("后台图片上传失败 for Barcode: {Barcode}, Index: {Index}: {Error}. 包裹状态不会更新。",
                                package.Barcode, package.Index, imageUploadError ?? "未知原因");
                        }
                        else
                        {
                            Log.Information("后台图片上传成功 for Barcode: {Barcode}, Index: {Index}", package.Barcode, package.Index);
                        }

                        try
                        {
                            // 尝试删除临时文件，无论上传是否成功
                            File.Delete(tempImagePath);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "删除临时图片文件 {TempImagePath} 失败", tempImagePath);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "后台上传包裹图片任务发生未捕获异常。Barcode: {Barcode}, Index: {Index}", package.Barcode, package.Index);
                    }
                });
            }
            else
            {
                Log.Warning("保存临时图片失败，无法启动后台图片上传 for Barcode: {Barcode}, Index: {Index}", package.Barcode, package.Index);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存临时图片或启动图片后台上传任务时发生错误 for Barcode: {Barcode}, Index: {Index}", package.Barcode, package.Index);
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
            chuteItem.Description = package.ChuteNumber == 0 ? "等待分配..." : "目标分拣位置";
        }

        var timeItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "当前时间");
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
            // 使用线程安全的计数器统计总数
            var totalCount = Interlocked.Read(ref _totalPackageCount);
            totalItem.Value = totalCount.ToString();
            totalItem.Description = $"累计处理 {totalCount} 个包裹";
        }

        // 更新NoRead异常统计
        var noReadItem = StatisticsItems.FirstOrDefault(static x => x.Label == "NoRead异常");
        if (noReadItem != null)
        {
            var noReadCount = Interlocked.Read(ref _noReadPackageCount);
            noReadItem.Value = noReadCount.ToString();
            noReadItem.Description = $"共有 {noReadCount} 个无法识别条码的包裹";
        }

        // 更新格口分配异常统计
        var chuteErrorItem = StatisticsItems.FirstOrDefault(static x => x.Label == "格口分配异常");
        if (chuteErrorItem != null)
        {
            var chuteErrorCount = Interlocked.Read(ref _chuteAllocationErrorCount);
            chuteErrorItem.Value = chuteErrorCount.ToString();
            chuteErrorItem.Description = $"共有 {chuteErrorCount} 个已上传但格口分配失败的包裹";
        }

        // 更新上传异常统计
        var uploadErrorItem = StatisticsItems.FirstOrDefault(static x => x.Label == "上传异常");
        if (uploadErrorItem != null)
        {
            var uploadErrorCount = Interlocked.Read(ref _uploadErrorCount);
            uploadErrorItem.Value = uploadErrorCount.ToString();
            uploadErrorItem.Description = $"共有 {uploadErrorCount} 个无法上传到笨鸟系统的包裹";
        }

        var efficiencyItem = StatisticsItems.FirstOrDefault(static x => x.Label == "预测效率");
        if (efficiencyItem != null)
        {
            // 获取最近的非noread包裹记录（最多取最近20个包裹）
            var recentPackages = PackageHistory
                .Where(p => !string.Equals(p.Barcode, "noread", StringComparison.OrdinalIgnoreCase))
                .Take(20)
                .ToList();
            if (recentPackages.Count >= 2)
            {
                // 计算最早和最新包裹的时间差（分钟）
                var timeSpan = recentPackages[0].CreateTime - recentPackages[^1].CreateTime;
                var minutes = timeSpan.TotalMinutes;

                if (minutes > 0)
                {
                    // 计算每分钟处理的包裹数
                    var packagesPerMinute = recentPackages.Count / minutes;
                    // 预测每小时处理量
                    var hourlyRate = (int)(packagesPerMinute * 60);

                    efficiencyItem.Value = hourlyRate.ToString();
                    efficiencyItem.Description = $"基于最近{recentPackages.Count}个包裹预测";
                }
                else
                {
                    efficiencyItem.Value = "0";
                    efficiencyItem.Description = "等待更多数据";
                }
            }
            else
            {
                efficiencyItem.Value = "0";
                efficiencyItem.Description = "等待更多数据";
            }
        }

        var avgTimeItem = StatisticsItems.FirstOrDefault(static x => x.Label == "平均处理时间");
        if (avgTimeItem == null) return;

        {
            // 获取最近的非noread包裹记录
            var recentPackages = PackageHistory
                .Where(p => !string.Equals(p.Barcode, "noread", StringComparison.OrdinalIgnoreCase))
                .Take(100)
                .ToList();

            if (recentPackages.Count != 0)
            {
                var avgTime = recentPackages.Average(static p => p.ProcessingTime);
                avgTimeItem.Value = avgTime.ToString("F0");
                avgTimeItem.Description = $"最近{recentPackages.Count}个有效包裹平均耗时";
            }
            else
            {
                avgTimeItem.Value = "0";
                avgTimeItem.Description = "暂无有效处理数据";
            }
        }
    }

    private static PackageInfo MergePackageInfos(IList<PackageInfo> buffer)
    {
        if (!buffer.Any()) throw new ArgumentException(@"缓冲区不能为空", nameof(buffer));

        var mergedPackage = buffer[0]; // 使用第一个包裹作为基础

        for (var i = 1; i < buffer.Count; i++)
        {
            var currentPackage = buffer[i];

            // 合并条码：优先使用非空且不是 "noread" 的条码
            if (!string.IsNullOrWhiteSpace(currentPackage.Barcode) &&
                !string.Equals(currentPackage.Barcode, "noread", StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(mergedPackage.Barcode) ||
                 string.Equals(mergedPackage.Barcode, "noread", StringComparison.OrdinalIgnoreCase)))
            {
                mergedPackage.SetBarcode(currentPackage.Barcode);
            }

            // 合并重量：优先使用大于0的重量
            if (currentPackage.Weight > 0 && mergedPackage.Weight <= 0)
            {
                mergedPackage.SetWeight(currentPackage.Weight);
            }

            // 合并长度：优先使用大于0的长度
            if (currentPackage.Length is > 0 &&
                mergedPackage.Length is null or <= 0)
            {
                // mergedPackage.SetLength(currentPackage.Length.Value);
                // 改为直接更新属性，因为 SetLength 不存在，并且 Length 的 setter 是 private
                // 需要找到合适的方式更新，可能需要修改 PackageInfo 或使用 SetDimensions
            }

            // 合并宽度：优先使用大于0的宽度
            if (currentPackage.Width is > 0 &&
                mergedPackage.Width is null or <= 0)
            {
                // mergedPackage.SetWidth(currentPackage.Width.Value);
            }

            // 合并高度：优先使用大于0的高度
            if (currentPackage.Height is > 0 &&
                mergedPackage.Height is null or <= 0)
            {
                // mergedPackage.SetHeight(currentPackage.Height.Value);
            }

            // 使用 SetDimensions 一次性设置尺寸
            var newLength = mergedPackage.Length ?? 0;
            var newWidth = mergedPackage.Width ?? 0;
            var newHeight = mergedPackage.Height ?? 0;

            if (currentPackage.Length is > 0 && newLength <= 0)
            {
                newLength = currentPackage.Length.Value;
            }

            if (currentPackage.Width is > 0 && newWidth <= 0)
            {
                newWidth = currentPackage.Width.Value;
            }

            if (currentPackage.Height is > 0 && newHeight <= 0)
            {
                newHeight = currentPackage.Height.Value;
            }

            // 只有当所有尺寸都有效时才调用 SetDimensions
            if (newLength > 0 && newWidth > 0 && newHeight > 0)
            {
                mergedPackage.SetDimensions(newLength, newWidth, newHeight);
            }
        }

        Log.Debug("Merged {Count} packages into one: {Barcode}, Weight: {Weight}, LWH: {L}x{W}x{H}",
            buffer.Count, mergedPackage.Barcode, mergedPackage.Weight,
            mergedPackage.Length, mergedPackage.Width, mergedPackage.Height);

        // 为合并后的包裹分配新的、线程安全的序号
        mergedPackage.Index = Interlocked.Increment(ref _nextMergedPackageIndex);
        Log.Debug("Assigned new merged index {NewIndex} to package {Barcode}", mergedPackage.Index,
            mergedPackage.Barcode);

        return mergedPackage;
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
            try
            {
                // 停止定时器（UI线程操作）
                if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
                    Application.Current.Dispatcher.Invoke(_timer.Stop);
                else
                    _timer.Stop();

                // 取消订阅事件
                _sortService.DeviceConnectionStatusChanged -= OnDeviceConnectionStatusChanged;
                _cameraService.ConnectionChanged -= OnCameraConnectionChanged;
                _beltSerialService.ConnectionStatusChanged -= OnBeltConnectionStatusChanged;

                // 释放订阅
                foreach (var subscription in _subscriptions)
                    try
                    {
                        subscription.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "释放常规订阅时发生错误");
                    }

                _subscriptions.Clear();

                // 释放扫码流订阅
                try
                {
                    _barcodeSubscription?.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "释放扫码流订阅时发生错误");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "释放资源时发生错误");
            }

        _disposed = true;
    }
}