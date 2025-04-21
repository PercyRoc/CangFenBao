using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
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
using Common.Data;

namespace BenFly.ViewModels.Windows;

internal class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly BenNiaoPackageService _benNiaoService;
    private readonly BenNiaoPreReportService _preReportService;
    private readonly ICameraService _cameraService;
    private readonly IDialogService _dialogService;
    private readonly ISettingsService _settingsService;
    private readonly IPendulumSortService _sortService;
    private readonly INotificationService _notificationService;
    private readonly IAudioService _audioService;
    private readonly IPackageDataService _packageDataService;
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
        BenNiaoPreReportService preReportService,
        ScannerStartupService scannerStartupService,
        INotificationService notificationService,
        IAudioService audioService,
        BeltSerialService beltSerialService,
        IPackageDataService packageDataService)
    {
        _dialogService = dialogService;
        _cameraService = cameraService;
        _settingsService = settingsService;
        _sortService = sortService;
        _benNiaoService = benNiaoService;
        _preReportService = preReportService;
        _packageDataService = packageDataService;
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
    public DelegateCommand OpenHistoryCommand { get; }

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
        _dialogService.ShowDialog("HistoryDialog", null, null, "HistoryWindow");
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
        // 移除阻止条码更新的条件判断
        // if (_barcodeScanCompletionSource is null || _barcodeScanCompletionSource.Task.IsCompleted) return;

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
        
        // 如果存在等待中的条码输入任务，则完成它
        if (_barcodeScanCompletionSource is not null && !_barcodeScanCompletionSource.Task.IsCompleted)
        {
            _barcodeScanCompletionSource.SetResult(barcode);
        }
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
            // 在方法开始时立即更新CurrentBarcode
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                CurrentBarcode = package.Barcode;
            });
            
            // 如果不是noread，播放成功音效
            if (!string.Equals(package.Barcode, "noread", StringComparison.OrdinalIgnoreCase))
            {
                _ = _audioService.PlayPresetAsync(AudioType.Success);
            }

            Log.Information("收到包裹信息：{Barcode}, 序号：{Index}", package.Barcode, package.Index);

            // 暂存原始图像，处理完成后清空
            var originalImage = package.Image;
            // 标记是否需要上传异常数据
            var uploadAsNoRead = false;
            // 标记是否跳过后续处理
            var skipFurtherProcessing = false;

            // 检查条码是否为 noread 或空
            var isNoReadOrEmpty = string.IsNullOrWhiteSpace(package.Barcode) || string.Equals(package.Barcode, "noread", StringComparison.OrdinalIgnoreCase);
            // 检查数据是否有效 (重量 > 0 且 尺寸 > 0)
            var isInvalidData = package.Weight <= 0 || 
                                !package.Length.HasValue || package.Length.Value <= 0 || 
                                !package.Width.HasValue || package.Width.Value <= 0 || 
                                !package.Height.HasValue || package.Height.Value <= 0;


            // 如果条码无效 或 数据无效
            if (isNoReadOrEmpty || isInvalidData)
            {
                // 播放错误音效
                _ = _audioService.PlayPresetAsync(AudioType.SystemError);

                // 停止皮带
                try
                {
                    _beltSerialService.StopBelt();
                    Log.Information("已发送停止皮带命令 (原因: {Reason})", isNoReadOrEmpty ? "NoRead/Empty Barcode" : "Invalid Weight/Volume");
                }
                catch (Exception ex)
                {
                    // 停止皮带失败，记录日志并尝试设置状态
                    var errorMsg = $"停止皮带失败: {ex.Message}";
                    Log.Error(ex, errorMsg);
                    package.SetStatus(PackageStatus.Error, errorMsg);
                    // 如果停止失败，可能不应该继续NoRead处理，标记跳过？看业务需求
                    // skipFurtherProcessing = true; 
                }

                // 更新UI历史记录和信息项
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        UpdatePackageInfoItems(package); // 先更新一次显示，即使是无效数据
                        PackageHistory.Insert(0, package);
                        while (PackageHistory.Count > 1000)
                            PackageHistory.RemoveAt(PackageHistory.Count - 1);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "更新UI历史记录时发生错误");
                    }
                });

                if (isNoReadOrEmpty)
                {
                    // --- 处理 NoRead/空条码 --- 
                    try
                    {
                        Log.Information("检测到 noread/空 条码，等待条码输入");
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            CurrentBarcode = string.Empty;
                            _notificationService.ShowWarning("检测到无法识别的条码，请扫描或手动输入条码后按回车");
                            if (Application.Current.MainWindow == null) return;
                            var textBox = FindBarcodeTextBox(Application.Current.MainWindow);
                            textBox?.Focus();
                        });

                        var enterKeyTcs = new TaskCompletionSource<bool>();
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                             if (Application.Current.MainWindow == null) return;
                            Application.Current.MainWindow.KeyDown += EnterKeyHandler;
                            return;

                            void EnterKeyHandler(object s, KeyEventArgs e)
                            {
                                if (e.Key != Key.Enter) return;
                                Application.Current.MainWindow!.KeyDown -= EnterKeyHandler;
                                enterKeyTcs.TrySetResult(true);
                            }
                        });

                        Log.Information("等待用户输入条码或按回车键跳过...");
                        var barcodeScanTask = WaitForBarcodeScanAsync();
                        var completedTask = await Task.WhenAny(barcodeScanTask, enterKeyTcs.Task);

                        string? newBarcode;
                        if (completedTask == barcodeScanTask)
                        {
                            newBarcode = await barcodeScanTask;
                            Log.Information("通过扫码方式获取新条码: {NewBarcode}", newBarcode);
                            if (!enterKeyTcs.Task.IsCompleted) await enterKeyTcs.Task;
                            Log.Information("用户已确认条码: {Barcode}", newBarcode);
                        }
                        else
                        {
                            newBarcode = CurrentBarcode;
                            if (!string.IsNullOrWhiteSpace(newBarcode))
                            {
                                Log.Information("用户直接输入新条码: {NewBarcode} 并按回车确认", newBarcode);
                            }
                            else
                            {
                                Log.Information("用户按下回车键，未输入条码，将作为异常数据上传");
                                _barcodeScanCompletionSource?.TrySetCanceled();
                                uploadAsNoRead = true;
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(newBarcode) && !uploadAsNoRead)
                        {
                            Log.Information("使用新条码更新包裹信息: {Barcode}", newBarcode);
                            package.SetBarcode(newBarcode);
                        }
                        else if (uploadAsNoRead)
                        {
                            Log.Information("将作为NoRead数据上传，原始条码: {OriginalBarcode}", package.Barcode);
                        }
                        else
                        {
                            Log.Warning("未明确获取到新条码且未标记为NoRead上传，将视为NoRead");
                            uploadAsNoRead = true;
                        }

                        // 重启皮带 (仅在NoRead处理完成后)
                        try
                        {
                            _beltSerialService.StartBelt();
                            Log.Information("NoRead处理完成，已发送启动皮带命令");
                        }
                        catch (Exception ex)
                        {
                            var errorMsg = $"启动皮带失败(NoRead后): {ex.Message}";
                            Log.Error(ex, errorMsg);
                            // 启动失败也记录到包裹信息中？可能意义不大，因为包裹信息已基本确定
                            // package.SetStatus(PackageStatus.Error, errorMsg); 
                            await Application.Current.Dispatcher.InvokeAsync(() => _notificationService.ShowError(errorMsg));
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        Log.Information("条码输入被取消");
                        uploadAsNoRead = true;
                        try { _beltSerialService.StartBelt(); Log.Information("条码输入取消，已发送启动皮带命令"); } 
                        catch (Exception ex) 
                        { 
                            var errorMsg = $"启动皮带失败(取消后): {ex.Message}";
                            Log.Error(ex, errorMsg); 
                            await Application.Current.Dispatcher.InvokeAsync(() => _notificationService.ShowError(errorMsg));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "等待条码输入时发生错误");
                        uploadAsNoRead = true;
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            _notificationService.ShowError("条码输入处理过程中发生错误");
                        });
                        try { _beltSerialService.StartBelt(); Log.Information("条码输入错误，已发送启动皮带命令");} 
                        catch (Exception startEx) 
                        { 
                            var errorMsg = $"启动皮带失败(错误后): {startEx.Message}";
                            Log.Error(startEx, errorMsg); 
                            await Application.Current.Dispatcher.InvokeAsync(() => _notificationService.ShowError(errorMsg));
                        }
                    }
                }
                else if (isInvalidData)
                {
                    // --- 处理无效数据 (重量/体积) --- 
                    var errorMsg = "包裹重量或体积数据无效";
                    Log.Error("包裹数据无效: {Barcode}, 重量: {Weight}, 长度: {Length}, 宽度: {Width}, 高度: {Height}", 
                            package.Barcode, package.Weight, package.Length, package.Width, package.Height);
                    package.SetStatus(PackageStatus.Error, errorMsg);
                    
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                         _notificationService.ShowError($"{errorMsg}，皮带已停止，请检查包裹或设备。");
                         UpdatePackageInfoItems(package); // 再次更新，确保状态和错误信息显示
                    });

                    // 跳过后续处理步骤，皮带保持停止
                    skipFurtherProcessing = true;
                    Log.Information("因数据无效，跳过包裹 {Barcode} 的后续处理流程。", package.Barcode);
                }
            }

             // ****** 开始处理包裹（如果未跳过） ******
            if (!skipFurtherProcessing)
            {
                Log.Information("开始处理包裹: {Barcode} (UploadAsNoRead: {IsNoRead})", package.Barcode, uploadAsNoRead);

                // 将变量声明移到此处
                bool benNiaoInteractionSuccess = true;
                string benNiaoErrorMessage = string.Empty;
                DateTime uploadTime = DateTime.MinValue;

                // 1. 分拣服务处理
                if (!uploadAsNoRead)
                {
                    try
                    {
                        _sortService.ProcessPackage(package);
                    }
                    catch (Exception ex)
                    {
                        var errorMsg = $"分拣处理异常: {ex.Message}";
                        Log.Error(ex, "分拣服务处理包裹 {Barcode} 时发生错误", package.Barcode);
                        package.SetStatus(PackageStatus.Error, errorMsg);
                        benNiaoInteractionSuccess = false; // 现在可以使用了
                        benNiaoErrorMessage = errorMsg;    // 现在可以使用了
                        package.SetChute(-1);
                    }
                }

                // 2. 笨鸟系统交互 (仅当分拣未失败时继续, 或者本身就是 NoRead)
                if (uploadAsNoRead)
                {
                    // 2.a 上传 NoRead 数据
                    Log.Information("调用笨鸟 UploadNoReadDataAsync for {Barcode}", package.Barcode);
                    // bool noReadUploadSuccess = await _benNiaoService.UploadNoReadDataAsync(package, originalImage);
                    var (noReadUploadSuccess, noReadErrorMessage) = await _benNiaoService.UploadNoReadDataAsync(package, originalImage);
                    if (!noReadUploadSuccess)
                    {
                        benNiaoInteractionSuccess = false;
                        // 使用服务返回的具体错误信息
                        benNiaoErrorMessage = string.IsNullOrWhiteSpace(noReadErrorMessage) ? "异常数据上传失败(未知原因)" : noReadErrorMessage;
                        package.SetStatus(PackageStatus.Error, benNiaoErrorMessage);
                        Log.Warning("笨鸟异常数据上传失败: {Barcode}, Error: {Error}", package.Barcode, benNiaoErrorMessage);
                    }
                    else
                    {
                        package.SetStatus(PackageStatus.NoRead); // 标记为已上传的NoRead
                        Log.Information("笨鸟异常数据上传成功: {Barcode}", package.Barcode);
                    }
                }
                else if(benNiaoInteractionSuccess) // 仅当条码有效且分拣未出错时，才进行后续笨鸟操作
                {
                    // 2.b 获取段码
                    try
                    {
                        var preReportData = _preReportService.GetPreReportData();
                        var preReportItem = preReportData?.FirstOrDefault(x => x.WaybillNum == package.Barcode);

                        if (preReportItem != null && !string.IsNullOrWhiteSpace(preReportItem.SegmentCode))
                        {
                            Log.Information("在预报数据中找到包裹 {Barcode} 的三段码: {SegmentCode}", package.Barcode, preReportItem.SegmentCode);
                            package.SetSegmentCode(preReportItem.SegmentCode);
                        }
                        else
                        {
                            Log.Information("预报数据未找到 {Barcode}，尝试实时查询...", package.Barcode);
                            // var segmentCode = await _benNiaoService.GetRealTimeSegmentCodeAsync(package.Barcode);
                            var (segmentCode, segmentError) = await _benNiaoService.GetRealTimeSegmentCodeAsync(package.Barcode);
                            if (!string.IsNullOrWhiteSpace(segmentCode))
                            {
                                Log.Information("通过实时查询获取到包裹 {Barcode} 的三段码: {SegmentCode}", package.Barcode, segmentCode);
                                package.SetSegmentCode(segmentCode);
                            }
                            else
                            {
                                // 获取段码失败，标记错误并记录消息
                                benNiaoInteractionSuccess = false; 
                                benNiaoErrorMessage = string.IsNullOrWhiteSpace(segmentError) ? "无法获取三段码(未知原因)" : segmentError;
                                package.SetStatus(PackageStatus.Error, benNiaoErrorMessage);
                                Log.Warning("无法获取包裹 {Barcode} 的三段码: {Error}", package.Barcode, benNiaoErrorMessage);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // 获取段码过程中发生未预期的异常
                        benNiaoInteractionSuccess = false; 
                        benNiaoErrorMessage = $"获取段码异常: {ex.Message}";
                        package.SetStatus(PackageStatus.Error, benNiaoErrorMessage);
                        Log.Error(ex, "获取包裹 {Barcode} 三段码时发生异常", package.Barcode);
                    }

                    // 2.c 上传包裹数据 (仅当获取段码成功时)
                    if (benNiaoInteractionSuccess)
                    {
                        Log.Information("调用笨鸟 UploadPackageDataAsync for {Barcode}", package.Barcode);
                        var (dataSuccess, time, dataErrorMessage) = await _benNiaoService.UploadPackageDataAsync(package);
                        if (!dataSuccess)
                        {
                            benNiaoInteractionSuccess = false;
                            benNiaoErrorMessage = string.IsNullOrEmpty(dataErrorMessage) ? "数据上传失败(未知原因)" : dataErrorMessage;
                            package.SetStatus(PackageStatus.Error, benNiaoErrorMessage);
                            Log.Warning("笨鸟数据上传失败: {Barcode}, Error: {Error}", package.Barcode, benNiaoErrorMessage);
                        }
                        else
                        {
                            uploadTime = time;
                            Log.Information("笨鸟数据上传成功: {Barcode}", package.Barcode);
                            // 初始状态设为成功，后续格口分配失败可覆盖
                            package.SetStatus(PackageStatus.Success);
                        }
                    }

                    // 2.d 启动图片上传 (仅当数据上传成功且有图片时)
                    if (benNiaoInteractionSuccess && originalImage != null)
                    {
                        Log.Information("准备启动后台图片上传 for {Barcode}", package.Barcode);
                        try
                        {
                            var tempImagePath = BenNiaoPackageService.SaveImageToTempFileAsync(originalImage, package.Barcode, uploadTime);
                            if (!string.IsNullOrWhiteSpace(tempImagePath))
                            {
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        // await _benNiaoService.UploadImageAsync(package.Barcode, uploadTime, tempImagePath);
                                        var (imageUploadSuccess, imageUploadError) = await _benNiaoService.UploadImageAsync(package.Barcode, uploadTime, tempImagePath);
                                        if (!imageUploadSuccess) 
                                        {
                                            Log.Warning("后台图片上传失败 for {Barcode}: {Error}. 包裹状态不会更新。", package.Barcode, imageUploadError ?? "未知原因");
                                        }
                                        else
                                        {
                                            Log.Information("后台图片上传成功 for {Barcode}", package.Barcode);
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
                                        Log.Error(ex, "后台上传包裹 {Barcode} 的图片任务发生未捕获异常", package.Barcode);
                                        // 也可以记录这个异常信息，但同样不更新包裹状态
                                    }
                                });
                                // Log.Information("已启动包裹 {Barcode} 的图片后台上传", package.Barcode); // 移到后台任务成功后记录?
                            }
                            else
                            {
                                Log.Warning("保存临时图片失败，无法启动后台图片上传 for {Barcode}", package.Barcode);
                                // 是否需要标记包裹错误？根据业务决定，目前只记录日志
                            }
                        }
                        catch (Exception ex)
                        {
                             Log.Error(ex, "保存临时图片或启动图片后台上传任务时发生错误 for {Barcode}", package.Barcode);
                             // 是否需要标记包裹错误？根据业务决定，目前只记录日志
                        }
                    }
                }

                switch (skipFurtherProcessing)
                {
                    // 3. 获取格口信息 (仅对非NoRead且笨鸟交互成功的包裹)
                    case false when !uploadAsNoRead && benNiaoInteractionSuccess:
                        try
                        {
                            var chuteConfig = _settingsService.LoadSettings<SegmentCodeRules>();
                            var chute = chuteConfig.GetChuteBySpaceSeparatedSegments(package.SegmentCode);
                            package.SetChute(chute);
                            Log.Information("包裹 {Barcode} 分配到格口 {Chute}，段码：{SegmentCode}", package.Barcode, chute, package.SegmentCode);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "获取格口号时发生错误：{Barcode}, {SegmentCode}", package.Barcode, package.SegmentCode);
                            package.SetStatus(PackageStatus.Error, $"格口分配错误: {ex.Message}");
                            package.SetChute(-1); // 分配到异常格口
                            Log.Information("包裹 {Barcode} 因获取格口号失败，分配到异常格口", package.Barcode);
                        }

                        break;
                    case false when !uploadAsNoRead && !benNiaoInteractionSuccess:
                        // 如果是非NoRead但分拣或笨鸟交互失败，也分配到异常口
                        package.SetChute(-1);
                        Log.Information("包裹 {Barcode} 因处理失败 ({FailureReason})，分配到异常格口", package.Barcode, benNiaoErrorMessage);
                        break;
                }
                // 对于NoRead包裹，通常不分配格口，保持默认或特定值

                // 4. 更新UI显示 - 将所有UI更新放在这里统一处理
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        // 更新条码显示
                        CurrentBarcode = package.Barcode;
                        
                        // 更新包裹信息项显示
                        UpdatePackageInfoItems(package);
                        
                        // 正常处理的包裹需要在这里添加到历史记录
                        if (!skipFurtherProcessing && !isNoReadOrEmpty && !isInvalidData)
                        {
                            PackageHistory.Insert(0, package);
                            while (PackageHistory.Count > 1000)
                                PackageHistory.RemoveAt(PackageHistory.Count - 1);
                        }
                        
                        UpdateStatistics();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "最终更新UI时发生错误");
                    }
                });
            }

            // ****** 在处理流程末尾异步保存数据 ******
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
             Log.Error(ex, "处理包裹信息时发生严重错误：{Barcode}", package.Barcode);
             package.SetStatus(PackageStatus.Error, $"系统异常: {ex.Message}");
             // 尝试更新UI以反映错误
            await Application.Current.Dispatcher.InvokeAsync(() => UpdatePackageInfoItems(package));
        }
        finally
        {
            // 清理图像资源
            if (package.Image != null) // package.Image 引用的是 originalImage
            {
                package.Image = null;
                Log.Debug("已清除包裹 {Barcode} 的图像引用", package.Barcode);
            }
             // 原始图像资源如果需要 Dispose，应在此处处理，但 BitmapSource 通常不需要显式 Dispose
             // originalImage?.Dispose();
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