using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Common.Data;
using Common.Models.Package;
using Common.Services.Settings;
using Common.Services.Ui;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Services;
using Serilog;
using SharedUI.Models;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Rookie.Services;
using Rookie.Models.Api;

namespace Rookie.ViewModels.Windows;

public class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly ICameraService _cameraService;
    private readonly IDialogService _dialogService;
    private readonly INotificationService _notificationService;
    private readonly IPackageDataService _packageDataService;
    private readonly ISettingsService _settingsService;
    private readonly IRookieApiService _rookieApiService;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly DispatcherTimer _timer;

    private string _currentBarcode = string.Empty;
    private BitmapSource? _currentImage;
    private bool _disposed;
    private SystemStatus _systemStatus = new();

    // Statistics Counters
    private long _totalPackageCount;
    private long _successPackageCount;
    private long _failedPackageCount;
    private long _peakRate;

    public MainWindowViewModel(
        IDialogService dialogService,
        ICameraService cameraService,
        ISettingsService settingsService,
        INotificationService notificationService,
        IPackageDataService packageDataService,
        IRookieApiService rookieApiService,
        PackageTransferService? packageTransferService = null)
    {
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _cameraService = cameraService ?? throw new ArgumentNullException(nameof(cameraService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _packageDataService = packageDataService ?? throw new ArgumentNullException(nameof(packageDataService));
        // packageTransferService is used directly in subscription setup

        OpenSettingsCommand = new DelegateCommand(ExecuteOpenSettings);
        OpenHistoryCommand = new DelegateCommand(ExecuteOpenHistory);

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        InitializeDeviceStatuses();
        InitializeStatisticsItems();
        InitializePackageInfoItems();

        _cameraService.ConnectionChanged += OnCameraConnectionChanged;
        _subscriptions.Add(_cameraService.ImageStream
            .ObserveOn(TaskPoolScheduler.Default)
            .Subscribe(imageData =>
            {
                try
                {
                    imageData.Freeze();
                    Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
                    {
                        try { CurrentImage = imageData; }
                        catch (Exception ex) { Log.Error(ex, "Error updating UI image."); }
                    });
                }
                catch (Exception ex) { Log.Error(ex, "Error processing image stream."); }
            }, ex => Log.Error(ex, "Error in camera image stream subscription.")));

        if (packageTransferService != null)
        {
            // Corrected: Use Dispatcher.BeginInvoke inside Subscribe like ZtCloudWarehous
            _subscriptions.Add(packageTransferService.PackageStream
                .Subscribe(package =>
                {
                    Application.Current?.Dispatcher.BeginInvoke(() => OnPackageInfo(package));
                },
                ex => Log.Error(ex, "Error in package stream subscription.")));
        }
        else
        {
            Log.Warning("PackageTransferService not available or not injected, cannot subscribe to package stream.");
        }

        Log.Information("Rookie MainWindowViewModel initialized.");
    }

    ~MainWindowViewModel()
    {
        Dispose(false);
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
        set => SetProperty(ref _currentImage, value);
    }

    public SystemStatus SystemStatus
    {
        get => _systemStatus;
        set => SetProperty(ref _systemStatus, value);
    }

    public ObservableCollection<PackageInfo> PackageHistory { get; } = [];
    public ObservableCollection<StatisticsItem> StatisticsItems { get; } = [];
    public ObservableCollection<DeviceStatus> DeviceStatuses { get; } = [];
    public ObservableCollection<PackageInfoItem> PackageInfoItems { get; } = [];

    private void ExecuteOpenSettings()
    {
        _dialogService.ShowDialog("SettingsDialog", new DialogParameters(), _ => { });
        
    }

    private void ExecuteOpenHistory()
    {
        _dialogService.ShowDialog("HistoryDialogView", new DialogParameters(), _ => { });
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        try
        {
            SystemStatus = SystemStatus.GetCurrentStatus();
            Application.Current?.Dispatcher.InvokeAsync(UpdateStatistics);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to update system status or statistics.");
        }
    }

    private void InitializeDeviceStatuses()
    {
        try
        {
            DeviceStatuses.Clear();
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "Camera",
                Status = "Disconnected",
                Icon = "Camera24",
                StatusColor = "#F44336"
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error initializing device status list.");
        }
    }

    private void InitializeStatisticsItems()
    {
        try
        {
            StatisticsItems.Clear();

            StatisticsItems.Add(new StatisticsItem(
                label: "Total Packages",
                value: "0",
                unit: "pcs",
                description: "Total packages processed",
                icon: "BoxMultiple24"
            ));

            StatisticsItems.Add(new StatisticsItem(
                label: "Success",
                value: "0",
                unit: "pcs",
                description: "Successfully processed packages",
                icon: "CheckmarkCircle24"
            ));

            StatisticsItems.Add(new StatisticsItem(
                label: "Failed",
                value: "0",
                unit: "pcs",
                description: "Failed packages (errors, timeouts)",
                icon: "ErrorCircle24"
            ));

            StatisticsItems.Add(new StatisticsItem(
                label: "Processing Rate",
                value: "0",
                unit: "pcs/hr",
                description: "Packages processed per hour",
                icon: "ArrowTrendingLines24"
            ));

            StatisticsItems.Add(new StatisticsItem(
                label: "Peak Rate",
                value: "0",
                unit: "pcs/hr",
                description: "Highest processing rate",
                icon: "Trophy24"
            ));

        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error initializing statistics items.");
        }
    }

    private void InitializePackageInfoItems()
    {
        try
        {
            PackageInfoItems.Clear();

            PackageInfoItems.Add(new PackageInfoItem(
                label: "Weight",
                value: "0.00",
                unit: "kg",
                description: "Package weight",
                icon: "Scales24"
            ));

            PackageInfoItems.Add(new PackageInfoItem(
                label: "Time",
                value: "--:--:--",
                description: "Processing time",
                icon: "Timer24"
            ));

            PackageInfoItems.Add(new PackageInfoItem(
                label: "Status",
                value: "Waiting",
                description: "Processing status",
                icon: "Info24"
            ));

        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error initializing package info items.");
        }
    }

    private void OnCameraConnectionChanged(string? deviceId, bool isConnected)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var cameraStatus = DeviceStatuses.FirstOrDefault(d => d.Name == "Camera");
                if (cameraStatus != null)
                {
                    cameraStatus.Status = isConnected ? "Connected" : "Disconnected";
                    cameraStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
                    Log.Information("Camera connection status updated: {Status}", cameraStatus.Status);
                }

            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error updating camera/device connection status on UI thread.");
            }
        });
    }

    private async void OnPackageInfo(PackageInfo package)
    {
        // Initial UI Update on Dispatcher (Remove history add from here)
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            CurrentBarcode = package.Barcode;
            UpdatePackageInfoItems(package); // Show initial data
            Interlocked.Increment(ref _totalPackageCount); // Count every package received
        });

        Log.Information("开始处理包裹: {Barcode}", package.Barcode);
        DestRequestResultParams? destinationResult;
        string finalChute = "ERR"; // Default error chute for reporting if needed
        string finalErrorMessage = "处理失败"; // Default error message

        try
        {
            // --- 1. 上报包裹信息 ---
            Log.Debug("上报包裹信息: {Barcode}", package.Barcode ?? "NoRead");
            bool uploadSuccess = await _rookieApiService.UploadParcelInfoAsync(package);
            if (!uploadSuccess)
            {
                Log.Error("上传包裹信息失败: {Barcode}", package.Barcode ?? "NoRead");
                finalErrorMessage = "上传包裹信息失败";
                package.SetStatus(PackageStatus.Error, finalErrorMessage);
                package.ErrorMessage = finalErrorMessage;
            }
            else
            {
                Log.Information("包裹信息上传成功: {Barcode}", package.Barcode ?? "NoRead");

                // --- 2. 请求目的地 ---
                Log.Debug("请求目的地: {Barcode}", package.Barcode ?? "NoRead");
                destinationResult = await _rookieApiService.RequestDestinationAsync(package.Barcode ?? "NoRead");

                if (destinationResult == null)
                {
                    Log.Error("请求目的地失败或API返回错误: {Barcode}", package.Barcode ?? "NoRead");
                    finalErrorMessage = "请求目的地失败";
                    package.SetStatus(PackageStatus.Error, finalErrorMessage);
                    package.ErrorMessage = finalErrorMessage;
                }
                else
                {
                    Log.Information("收到目的地信息: {Barcode}, Chute: {ChuteCode}, ErrorCode: {ErrorCode}, FinalBarcode: {FinalBarcode}",
                        package.Barcode ?? "NoRead", destinationResult.ChuteCode, destinationResult.ErrorCode, destinationResult.FinalBarcode);

                    if (!string.IsNullOrWhiteSpace(destinationResult.FinalBarcode) && destinationResult.FinalBarcode != package.Barcode)
                    {
                         Log.Information("DCS 返回不同的最终条码: {OldBarcode} -> {NewBarcode}", package.Barcode, destinationResult.FinalBarcode);
                         // package.SetBarcode(destinationResult.FinalBarcode); // Use SetBarcode if needed
                    }

                    if (destinationResult.ErrorCode == 0) // 0 = 正常
                    {
                        if (int.TryParse(destinationResult.ChuteCode, out var chuteNumber))
                        {
                            package.SetChute(chuteNumber);
                            package.SetStatus(PackageStatus.Success);
                            package.ErrorMessage = null; 
                            finalChute = destinationResult.ChuteCode;
                            Log.Information("包裹 {Barcode} 分配到格口: {Chute}", package.Barcode ?? "NoRead", finalChute);
                        }
                        else
                        {
                            Log.Error("无法解析目的地格口 '{ChuteCode}' 为整数: {Barcode}", destinationResult.ChuteCode, package.Barcode ?? "NoRead");
                            finalErrorMessage = $"无效格口: {destinationResult.ChuteCode}";
                            package.SetStatus(PackageStatus.Error, finalErrorMessage);
                            package.ErrorMessage = finalErrorMessage;
                        }
                    }
                    else // DCS returned a business error
                    {
                        var dcsError = MapDcsErrorCodeToString(destinationResult.ErrorCode);
                        Log.Error("DCS 目的地请求错误: {Barcode}, ErrorCode: {ErrorCode} ({ErrorString})",
                                  package.Barcode ?? "NoRead", destinationResult.ErrorCode, dcsError);
                        var mappedStatus = MapDcsErrorCodeToPackageStatus(destinationResult.ErrorCode); 
                        package.SetStatus(mappedStatus, dcsError);
                        package.ErrorMessage = dcsError;
                        finalErrorMessage = package.ErrorMessage;
                        finalChute = destinationResult.ChuteCode ?? "ERR"; 
                    }
                }
            }

            // --- 3. 上报分拣结果 ---
            // Note: We now use package.Status which reflects the outcome of the destination request
            Log.Debug("上报分拣结果: {Barcode}, Chute: {Chute}, Success: {StatusBool}",
                      package.Barcode ?? "NoRead", finalChute, package.Status == PackageStatus.Success);
            bool reportSuccess = await _rookieApiService.ReportSortResultAsync(
                package.Barcode ?? "NoRead",
                finalChute,
                package.Status == PackageStatus.Success, // Report based on final PackageStatus
                package.Status == PackageStatus.Success ? null : package.ErrorMessage ?? finalErrorMessage); // Use actual error message if available

            if (!reportSuccess)
            {
                Log.Error("上报分拣结果失败: {Barcode}", package.Barcode ?? "NoRead");
                // Consider if this failure should change the package status or just be logged.
            }
            else
            {
                Log.Information("分拣结果上报成功: {Barcode}", package.Barcode ?? "NoRead");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹 {Barcode} 时发生意外错误。", package.Barcode ?? "NoRead");
            try 
            {
                finalErrorMessage = $"处理异常: {ex.Message.Split(['\r', '\n'])[0]}";
                package.SetStatus(PackageStatus.Error, finalErrorMessage); 
                package.ErrorMessage = finalErrorMessage;
            } 
            catch { /* Ignore */ }
            
            // Attempt to report failure after exception
            try
            {
                Log.Debug("尝试在异常后上报失败结果: {Barcode}, Chute: {Chute}", package.Barcode ?? "NoRead", finalChute);
                await _rookieApiService.ReportSortResultAsync(
                    package.Barcode ?? "NoRead",
                    finalChute, 
                    false,      
                    finalErrorMessage);
            }
            catch(Exception reportEx)
            {
                Log.Error(reportEx, "在主处理异常后上报分拣结果也失败: {Barcode}", package.Barcode ?? "NoRead");
            }
        }
        finally
        {
             Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                // Update UI elements based on final package status
                UpdatePackageInfoItems(package); 

                // Update statistics counters based on final package status
                if (package.Status == PackageStatus.Success) 
                { 
                    Interlocked.Increment(ref _successPackageCount); 
                }
                else 
                { 
                    Interlocked.Increment(ref _failedPackageCount);
                }
                UpdateStatistics();

                // Add package to history list AFTER all processing and UI updates
                PackageHistory.Insert(0, package); 
                if (PackageHistory.Count > 500)
                {
                    PackageHistory.RemoveAt(PackageHistory.Count - 1);
                }

            });

            package.ReleaseImage(); 
            Log.Information("包裹 {Barcode} 处理流程结束, 最终状态: {Status}", package.Barcode ?? "NoRead", package.Status);
        }
    }

    // Helper to map DCS error codes to string
    private static string MapDcsErrorCodeToString(int errorCode)
    {
        return errorCode switch
        {
            1 => "无规则",
            2 => "无任务",
            3 => "重量异常",
            4 => "业务拦截",
            _ => $"未知错误码 ({errorCode})"
        };
    }
    
    // Helper to map DCS error codes to PackageStatus enum
    private static PackageStatus MapDcsErrorCodeToPackageStatus(int errorCode)
    {
        return errorCode switch
        {
            1 => PackageStatus.Failed, // No Rule
            2 => PackageStatus.Failed, // No Task
            3 => PackageStatus.Error, // Weight Exception
            4 => PackageStatus.Failed, // Business Intercept
            _ => PackageStatus.Error   // Default to Error for unknown codes
        };
    }

    private void UpdatePackageInfoItems(PackageInfo package)
    {
        var weightItem = PackageInfoItems.FirstOrDefault(i => i.Label == "Weight");
        if (weightItem != null) { weightItem.Value = package.Weight.ToString("F2"); }

        // Removed: Chute item update
        /*
        var chuteItem = PackageInfoItems.FirstOrDefault(i => i.Label == "Chute");
        if (chuteItem != null) { chuteItem.Value = package.ChuteNumber > 0 ? package.ChuteNumber.ToString() : "--"; }
        */

        var timeItem = PackageInfoItems.FirstOrDefault(i => i.Label == "Time");
        if (timeItem != null) { timeItem.Value = package.CreateTime.ToString("HH:mm:ss"); }

        var statusItem = PackageInfoItems.FirstOrDefault(i => i.Label == "Status");
        if (statusItem == null) return;
        statusItem.Value = package.StatusDisplay;
        statusItem.Description = package.ErrorMessage ?? package.StatusDisplay;
        statusItem.StatusColor = package.Status switch
        {
            PackageStatus.Success => "#4CAF50",
            PackageStatus.Error => "#F44336",
            PackageStatus.Timeout => "#FF9800",
            PackageStatus.NoRead => "#FFEB3B",
            _ => "#2196F3"
        };
    }

    private void UpdateStatistics()
    {
        var totalItem = StatisticsItems.FirstOrDefault(i => i.Label == "Total Packages");
        if (totalItem != null) totalItem.Value = _totalPackageCount.ToString();

        var successItem = StatisticsItems.FirstOrDefault(i => i.Label == "Success");
        if (successItem != null) successItem.Value = _successPackageCount.ToString();

        var failedItem = StatisticsItems.FirstOrDefault(i => i.Label == "Failed");
        if (failedItem != null) failedItem.Value = _failedPackageCount.ToString();

        var rateItem = StatisticsItems.FirstOrDefault(i => i.Label == "Processing Rate");
        if (rateItem == null) return;
        {
            var minuteAgo = DateTime.Now.AddMinutes(-1);
            var lastMinuteCount = PackageHistory.Count(p => p.CreateTime > minuteAgo);
            var hourlyRate = lastMinuteCount * 60;
            rateItem.Value = hourlyRate.ToString();

            if (hourlyRate <= _peakRate) return;
            _peakRate = hourlyRate;
            var peakRateItem = StatisticsItems.FirstOrDefault(i => i.Label == "Peak Rate");
            if (peakRateItem != null) peakRateItem.Value = _peakRate.ToString();
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            Log.Information("Disposing Rookie MainWindowViewModel resources.");
            try
            {
                _timer.Stop();
                _timer.Tick -= Timer_Tick;

                _cameraService.ConnectionChanged -= OnCameraConnectionChanged;

                foreach (var subscription in _subscriptions)
                {
                    subscription.Dispose();
                }
                _subscriptions.Clear();

                PackageHistory.Clear();
                StatisticsItems.Clear();
                DeviceStatuses.Clear();
                PackageInfoItems.Clear();

                Log.Information("Rookie MainWindowViewModel resources disposed.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error during MainWindowViewModel disposal.");
            }
        }

        _disposed = true;
    }
}