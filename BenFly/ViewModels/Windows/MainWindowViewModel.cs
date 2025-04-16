using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Common.Models.Package;
using Common.Models.Settings.ChuteRules;
using Common.Services.Settings;
using Common.Services.Ui;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Scanner;
using DeviceService.DataSourceDevices.Services;
using BenFly.Services;
using Common.Models.Settings.Sort.PendulumSort;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;
using SharedUI.Models;
using SortingServices.Pendulum;
using Common.Services.Audio;
using DeviceService.DataSourceDevices.Belt;
using Prism.Services.Dialogs;

namespace BenFly.ViewModels.Windows;

internal class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly BenNiaoPackageService _benNiaoService;
    private readonly ICameraService _cameraService;
    private readonly IDialogService _dialogService;
    private readonly ISettingsService _settingsService;
    private readonly IPendulumSortService _sortService;
    private readonly INotificationService _notificationService;
    private readonly IAudioService _audioService;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly IDisposable? _barcodeSubscription; // Subscription for the barcode stream

    private readonly DispatcherTimer _timer;
    private TaskCompletionSource<string>? _barcodeScanCompletionSource;

    private string _currentBarcode = string.Empty;

    private BitmapSource? _currentImage;

    private bool _disposed;

    private SystemStatus _systemStatus = new();

    private readonly BeltSerialService _beltSerialService;

    public MainWindowViewModel(
        IDialogService dialogService,
        ICameraService cameraService,
        ISettingsService settingsService,
        IPendulumSortService sortService,
        PackageTransferService packageTransferService,
        BenNiaoPackageService benNiaoService,
        ScannerStartupService scannerStartupService,
        INotificationService notificationService,
        IAudioService audioService, BeltSerialService beltSerialService)
    {
        _dialogService = dialogService;
        _cameraService = cameraService;
        _settingsService = settingsService;
        _sortService = sortService;
        _benNiaoService = benNiaoService;
        var scannerService = scannerStartupService.GetScannerService();
        _notificationService = notificationService;
        _audioService = audioService;
        _beltSerialService = beltSerialService;
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
    }

    public DelegateCommand OpenSettingsCommand { get; }

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
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "皮带",
                Status = "未连接",
                Icon = "AlignStartVertical20",
                StatusColor = "#F44336"
            });
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

            // 更新皮带初始状态
            UpdateBeltStatus(_beltSerialService.IsOpen);
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

            beltStatus.Status = isConnected ? "已连接" : "已断开";
            beltStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
        });
    }

    private void InitializeStatisticsItems()
    {
        StatisticsItems.Add(new StatisticsItem
        {
            Label = "总包裹数",
            Value = "0",
            Unit = "个",
            Description = "累计处理包裹总数",
            Icon = "CubeMultiple24"
        });

        StatisticsItems.Add(new StatisticsItem
        {
            Label = "异常数",
            Value = "0",
            Unit = "个",
            Description = "处理异常的包裹数量",
            Icon = "AlertOff24"
        });

        StatisticsItems.Add(new StatisticsItem
        {
            Label = "预测效率",
            Value = "0",
            Unit = "个/小时",
            Description = "预计每小时处理量",
            Icon = "ArrowTrending24"
        });

        StatisticsItems.Add(new StatisticsItem
        {
            Label = "平均处理时间",
            Value = "0",
            Unit = "ms",
            Description = "单个包裹平均处理时间",
            Icon = "Timer24"
        });
    }

    private void InitializePackageInfoItems()
    {
        PackageInfoItems.Add(new PackageInfoItem
        {
            Label = "重量",
            Value = "--",
            Unit = "kg",
            Description = "包裹重量",
            Icon = "Scales24"
        });

        PackageInfoItems.Add(new PackageInfoItem
        {
            Label = "尺寸",
            Value = "--",
            Unit = "cm",
            Description = "长×宽×高",
            Icon = "Ruler24"
        });

        PackageInfoItems.Add(new PackageInfoItem
        {
            Label = "段码",
            Value = "--",
            Description = "三段码信息",
            Icon = "BarcodeScanner24"
        });

        PackageInfoItems.Add(new PackageInfoItem
        {
            Label = "分拣口",
            Value = "--",
            Description = "目标分拣位置",
            Icon = "ArrowCircleDown24"
        });

        PackageInfoItems.Add(new PackageInfoItem
        {
            Label = "处理时间",
            Value = "--",
            Unit = "ms",
            Description = "系统处理耗时",
            Icon = "Timer24"
        });

        PackageInfoItems.Add(new PackageInfoItem
        {
            Label = "当前时间",
            Value = "--:--:--",
            Description = "包裹处理时间",
            Icon = "Clock24"
        });
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
        if (_barcodeScanCompletionSource is null || _barcodeScanCompletionSource.Task.IsCompleted) return;

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

    private async Task<string> WaitForBarcodeScanAsync()
    {
        _barcodeScanCompletionSource = new TaskCompletionSource<string>();

        try
        {
            // 显示等待扫码提示
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                // 清空条码输入框
                CurrentBarcode = string.Empty;

                // 查找主窗口中的条码输入控件并设置焦点
                if (Application.Current.MainWindow is null) return;

                // 查找包含条码显示的控件
                var textBox = FindBarcodeTextBox(Application.Current.MainWindow);
                if (textBox is not null)
                {
                    // 设置焦点
                    textBox.Focus();
                    Log.Debug("已将焦点设置到条码输入控件");
                }
                else
                {
                    Log.Warning("未找到条码输入控件，无法设置焦点");
                }
            });

            // 等待用户输入或扫码，并按回车确认
            return await _barcodeScanCompletionSource.Task;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "等待条码输入时发生错误");
            throw;
        }
    }

    /// <summary>
    ///     查找条码输入控件
    /// </summary>
    /// <param name="parent">父控件</param>
    /// <returns>条码输入控件</returns>
    private TextBox? FindBarcodeTextBox(DependencyObject parent)
    {
        // 获取父控件的所有子控件
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);

            // 如果是TextBox且名称或Tag包含Barcode关键字
            if (child is TextBox textBox)
            {
                var name = textBox.Name.ToLowerInvariant();
                var tag = textBox.Tag?.ToString()?.ToLowerInvariant() ?? string.Empty;

                if (name.Contains("barcode") || tag.Contains("barcode") ||
                    textBox.Text == CurrentBarcode)
                    return textBox;
            }

            // 递归查找子控件
            var result = FindBarcodeTextBox(child);
            if (result is not null) return result;
        }

        return null;
    }

    /// <summary>
    /// 处理用户手动输入或巴枪扫码的条码（按回车键触发）
    /// </summary>
    public void OnBarcodeInput()
    {
        if (_barcodeScanCompletionSource is not { Task.IsCompleted: false }) return;
        var barcode = CurrentBarcode;
        // 移除提示文本
        barcode = barcode.Replace(" (请按回车键确认...)", "").Replace(" (请按回车键继续...)", "");

        if (string.IsNullOrWhiteSpace(barcode)) return;
        Log.Information("用户通过输入框确认条码：{Barcode}", barcode);
        _barcodeScanCompletionSource.SetResult(barcode);
    }

    private async void OnPackageInfo(PackageInfo package)
    {
        try
        {
            // 如果不是noread，播放成功音效
            if (!string.Equals(package.Barcode, "noread", StringComparison.OrdinalIgnoreCase))
            {
                _ = _audioService.PlayPresetAsync(AudioType.Success);
            }

            Log.Information("收到包裹信息：{Barcode}, 序号：{Index}", package.Barcode, package.Index);

            // 检查条码是否为 noread
            if (string.Equals(package.Barcode, "noread", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    // 播放错误音效
                    _ = _audioService.PlayPresetAsync(AudioType.SystemError);

                    Log.Information("检测到 noread 条码，停止皮带并等待条码输入");
                    try
                    {
                        _beltSerialService.StopBelt();
                        Log.Information("已发送停止皮带命令");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "发送停止皮带命令时发生错误");
                    }

                    // 将noread包裹添加到历史记录
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            // 更新包裹信息项显示
                            UpdatePackageInfoItems(package);

                            // 添加到历史记录
                            PackageHistory.Insert(0, package);
                            while (PackageHistory.Count > 1000) // 保持最近1000条记录
                                PackageHistory.RemoveAt(PackageHistory.Count - 1);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "将noread包裹添加到历史记录时发生错误");
                        }

                        // 清空条码输入框
                        CurrentBarcode = string.Empty;
                        // 显示警告通知
                        _notificationService.ShowWarning("检测到无法识别的条码，请使用巴枪扫描或手动输入条码");

                        // 查找并设置条码输入框焦点
                        if (Application.Current.MainWindow == null) return;
                        var textBox = FindBarcodeTextBox(Application.Current.MainWindow);
                        textBox?.Focus();
                    });

                    // 创建等待回车键的任务
                    var enterKeyTcs = new TaskCompletionSource<bool>();

                    // 在UI线程上设置事件处理
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (Application.Current.MainWindow == null) return;

                        Application.Current.MainWindow.KeyDown += EnterKeyHandler;
                        return;

                        void EnterKeyHandler(object s, KeyEventArgs e)
                        {
                            if (e.Key != Key.Enter) return;

                            Application.Current.MainWindow!.KeyDown -= EnterKeyHandler;
                            enterKeyTcs.SetResult(true);
                        }
                    });

                    Log.Information("等待用户输入条码或按回车键跳过...");

                    // 同时等待巴枪扫码和回车键
                    var barcodeScanTask = WaitForBarcodeScanAsync();
                    var completedTask = await Task.WhenAny(barcodeScanTask, enterKeyTcs.Task);

                    string? newBarcode = null;

                    // 如果是回车键先完成，并且没有输入条码
                    if (completedTask == enterKeyTcs.Task && string.IsNullOrWhiteSpace(CurrentBarcode))
                    {
                        Log.Information("用户按下回车键，跳过条码输入，将继续使用noread条码");
                        // 清理巴枪扫码任务
                        _barcodeScanCompletionSource?.TrySetCanceled();
                    }
                    else
                    {
                        // 条码输入完成，创建新的包裹记录
                        if (completedTask == barcodeScanTask)
                        {
                            // 通过WaitForBarcodeScanAsync获取条码
                            newBarcode = await barcodeScanTask;
                            Log.Information("通过扫码方式获取新条码: {NewBarcode}", newBarcode);

                            // 等待用户按回车键确认
                            if (!enterKeyTcs.Task.IsCompleted)
                            {
                                Log.Information("等待用户按回车键确认条码...");
                                await enterKeyTcs.Task;
                            }

                            Log.Information("用户已确认条码: {Barcode}", newBarcode);
                        }
                        else
                        {
                            // 通过直接在界面输入获取条码
                            newBarcode = CurrentBarcode;
                            if (!string.IsNullOrWhiteSpace(newBarcode))
                            {
                                Log.Information("用户直接输入新条码: {NewBarcode} 并按回车确认", newBarcode);
                            }
                        }
                    }

                    // 如果获取到了新条码，创建新的包裹记录
                    if (!string.IsNullOrWhiteSpace(newBarcode))
                    {
                        Log.Information("创建新的包裹记录，条码: {Barcode}", newBarcode);
                        // 创建新的包裹记录，复制原有包裹的信息
                        var newPackage = PackageInfo.Create();
                        newPackage.SetBarcode(newBarcode);
                        newPackage.SetWeight(package.Weight);
                        newPackage.SetDimensions(
                            package.Length ?? 0,
                            package.Width ?? 0,
                            package.Height ?? 0
                        );
                        newPackage.Index = package.Index;

                        // 更新包裹对象为新的包裹
                        package = newPackage;
                    }
                    else
                    {
                        Log.Information("未获取到新条码，将继续使用原始noread条码");
                    }

                    // 发送启动皮带命令
                    try
                    {
                        _beltSerialService.StartBelt();
                        Log.Information("已发送启动皮带命令");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "发送启动皮带命令时发生错误");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "等待条码输入时发生错误");
                    // 发生错误时继续使用原始条码，并启动皮带
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        CurrentBarcode = "noread (输入错误)";
                        _notificationService.ShowError("条码输入处理过程中发生错误，将使用原始noread条码");
                    });

                    try
                    {
                        _beltSerialService.StartBelt();
                        Log.Information("输入错误，已发送启动皮带命令");
                    }
                    catch (Exception startEx)
                    {
                        Log.Error(startEx, "发送启动皮带命令时发生错误");
                    }
                }
            }

            // 处理包裹（无论是原始条码还是更新后的条码）
            Log.Information("开始处理包裹：{Barcode}", package.Barcode);

            // 1. 分拣处理
            _sortService.ProcessPackage(package);

            // 2. 通过笨鸟系统服务获取三段码并处理上传
            var (benNiaoSuccess, errorMessage) = await _benNiaoService.ProcessPackageAsync(package);
            if (!benNiaoSuccess)
            {
                Log.Warning("笨鸟系统处理包裹失败：{Barcode}, 错误：{Error}", package.Barcode, errorMessage);
                package.SetStatus(PackageStatus.Error, $"{errorMessage}");
                // 设置为异常格口（使用-1表示异常格口）
                package.SetChute(-1);
                Log.Information("包裹 {Barcode} 因笨鸟系统处理失败，分配到异常格口", package.Barcode);
            }
            else
            {
                // 处理成功，设置状态为分拣成功
                package.SetStatus(PackageStatus.Success);
                Log.Information("包裹 {Barcode} 笨鸟系统处理成功", package.Barcode);
            }

            // 笨鸟系统处理完成后释放图像资源
            if (package.Image != null)
            {
                // package.Image.Dispose();
                package.Image = null;
                Log.Debug("已释放包裹 {Barcode} 的图像资源", package.Barcode);
            }

            // 3. 获取格口信息
            try
            {
                // 如果已经分配到异常格口，则跳过正常格口分配逻辑
                if (package.ChuteNumber == -1)
                {
                    Log.Information("包裹 {Barcode} 已分配到异常格口，跳过正常格口分配", package.Barcode);
                }
                else
                {
                    var chuteConfig = _settingsService.LoadSettings<SegmentCodeRules>();
                    var chute = chuteConfig.GetChuteBySpaceSeparatedSegments(package.SegmentCode);
                    package.SetChute(chute);
                    Log.Information("包裹 {Barcode} 分配到格口 {Chute}，段码：{SegmentCode}",
                        package.Barcode, chute, package.SegmentCode);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "获取格口号时发生错误：{Barcode}, {SegmentCode}",
                    package.Barcode, package.SegmentCode);
                package.SetStatus(PackageStatus.Error, $"{ex.Message}");
                // 异常时也分配到异常格口，使用-1表示
                package.SetChute(-1);
                Log.Information("包裹 {Barcode} 因获取格口号失败，分配到异常格口", package.Barcode);
            }

            // 4. 更新UI显示
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    CurrentBarcode = package.Barcode;
                    UpdatePackageInfoItems(package);
                    // 更新统计信息和历史包裹列表
                    PackageHistory.Insert(0, package);
                    while (PackageHistory.Count > 1000) // 保持最近1000条记录
                        PackageHistory.RemoveAt(PackageHistory.Count - 1);
                    // 更新统计数据
                    UpdateStatistics();
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
            package.SetStatus(PackageStatus.Error, $"{ex.Message}");
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
            // 计算非noread包裹的数量
            var validPackageCount =
                PackageHistory.Count(p => !string.Equals(p.Barcode, "noread", StringComparison.OrdinalIgnoreCase));
            totalItem.Value = validPackageCount.ToString();
            totalItem.Description = $"累计处理 {validPackageCount} 个有效包裹";
        }

        var errorItem = StatisticsItems.FirstOrDefault(static x => x.Label == "异常数");
        if (errorItem != null)
        {
            // 计算非noread且存在错误的包裹数量
            var errorCount = PackageHistory.Count(p =>
                !string.IsNullOrEmpty(p.ErrorMessage) &&
                !string.Equals(p.Barcode, "noread", StringComparison.OrdinalIgnoreCase));
            errorItem.Value = errorCount.ToString();
            errorItem.Description = $"共有 {errorCount} 个异常包裹";
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

    protected virtual void Dispose(bool disposing)
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