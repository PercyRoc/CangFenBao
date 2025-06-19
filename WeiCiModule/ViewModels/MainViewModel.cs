using System.Collections.ObjectModel;
using System.Windows.Threading;
using Common.Models.Package;
using Common.Services.Settings;
using Serilog;
using WeiCiModule.Services;
using Application = System.Windows.Application;
using Microsoft.Win32;
using System.IO;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using Camera.Services.Implementations.TCP;
using Common.Models;
using History.Data;
using History.Views.Dialogs;
using WeiCiModule.Models;
using WeiCiModule.Models.Settings;

namespace WeiCiModule.ViewModels;

public class MainViewModel : BindableBase, IDisposable
{
    private readonly IDialogService _dialogService;
    private readonly TcpCameraService _cameraService;
    private readonly IModuleConnectionService _moduleConnectionService;
    private readonly ISettingsService _settingsService;
    private readonly IPackageHistoryDataService _packageHistoryDataService;
    private readonly IDisposable? _packageStreamSubscription;
    private readonly DispatcherTimer _timer;
    private string _currentBarcode = string.Empty;
    private SystemStatus _systemStatus = new();
    private string _searchImportedText = string.Empty;
    
    // 统计信息相关字段
    private int _totalPackages = 0;
    private int _successPackages = 0;
    private int _failurePackages = 0;
    private DateTime _firstPackageTime = DateTime.MinValue;

    public ObservableCollection<PackageInfo> PackageHistory { get; } = [];
    public ObservableCollection<StatisticsItem> StatisticsItems { get; } = [];
    public ObservableCollection<DeviceStatus> DeviceStatuses { get; } = [];
    public ObservableCollection<PackageInfoItem> PackageInfoItems { get; } = [];
    public ObservableCollection<BarcodeChuteMapping> AllImportedMappings { get; } = [];
    public ObservableCollection<BarcodeChuteMapping> FilteredImportedMappings { get; } = [];
    
    public MainViewModel(IDialogService dialogService, ISettingsService settingsService,
        IModuleConnectionService moduleConnectionService, TcpCameraService cameraService,
        IPackageHistoryDataService packageHistoryDataService)
    {
        _dialogService = dialogService;
        _settingsService = settingsService;
        _moduleConnectionService = moduleConnectionService;
        _cameraService = cameraService;
        _packageHistoryDataService = packageHistoryDataService;

        OpenSettingsCommand = new DelegateCommand(ExecuteOpenSettings);
        OpenHistoryCommand = new DelegateCommand(ExecuteOpenHistory);
        ImportConfigCommand = new DelegateCommand(ExecuteImportConfig);
        SearchImportedCommand = new DelegateCommand(ExecuteSearchImported);
        ResetStatisticsCommand = new DelegateCommand(ExecuteResetStatistics);

        // 初始化系统状态更新定时器
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        // 初始化UI集合
        InitializeDeviceStatuses();
        InitializeStatisticsItems();
        InitializePackageInfoItems();

        // 订阅外部服务状态变更
        _moduleConnectionService.ConnectionStateChanged += OnModuleConnectionChanged;
        _cameraService.ConnectionChanged += OnCameraConnectionChanged;

        // --- 基于"信号驱动"的最终响应式处理流程 ---

        // 1. 获取源头事件流
        var signalStream = _moduleConnectionService.TriggerSignalStream.Timestamp();
        var cameraStream = _cameraService.PackageStream;

        // 2. [关键] 使用 Publish().RefCount()
        //    这使得 cameraStream 可以被多个并行的"期望"安全地订阅，
        //    并且保证每个相机数据只会被一个"期望"消耗掉。
        var sharedCameraStream = cameraStream.Publish().RefCount();
    
        // 3. 定义数据流处理管道
        _packageStreamSubscription = signalStream
            .Select(timedSignal => 
            {
                Log.Debug("信号 {SignalNumber} 到达，开始等待相机数据...", timedSignal.Value);

                // 为这个信号创建一个"期望流"
                return sharedCameraStream
                    .Take(1) // 我们只关心这个信号对应的"下一个"相机数据
                    .Timeout(TimeSpan.FromMilliseconds(GetMaxWaitTime())) // 为这个期望设置独立的超时
                    .Select(package => new { Signal = timedSignal, Package = package, TimedOut = false }) // 成功，包装成一个对象
                    .Catch((TimeoutException ex) => 
                    {
                        // 超时发生，也包装成一个对象，但标记为超时
                        Log.Warning("❌ 信号 {SignalNumber} 在 {MaxWaitTime}ms 内等待包裹超时！", timedSignal.Value, GetMaxWaitTime());
                        return Observable.Return(new { Signal = timedSignal, Package = (PackageInfo)null, TimedOut = true });
                    });
            })
            .Concat() // <--- 使用 Concat() 保证严格按照信号的顺序来处理结果
            .ObserveOn(SynchronizationContext.Current!) // 切换到UI线程进行后续所有操作
            .Subscribe(
                result => 
                {
                    try 
                    {
                        if (result.TimedOut)
                        {
                            // 处理超时情况
                            Log.Information("超时信号 {Signal} 的分拣指令发送至异常口。", result.Signal.Value);
                            _ = _moduleConnectionService.SendSortingCommandAsync(result.Signal.Value, (byte)GetExceptionChute());

                            // 更新UI和统计
                            var timeoutPackage = PackageInfo.Create();
                            timeoutPackage.Index = result.Signal.Value;
                            timeoutPackage.SetStatus("timeout");
                            UpdateStatistics(timeoutPackage);
                            UpdatePackageInfoItems_Final(timeoutPackage);
                        }
                        else
                        {
                            // 匹配成功，进行时间校验
                            var package = result.Package;
                            var timeDiffMs = (package.CreateTime - result.Signal.Timestamp.DateTime).TotalMilliseconds;
                            
                            Log.Debug("配对成功: 信号={Signal}, 条码={Barcode}。开始时间校验...", result.Signal.Value, package.Barcode);

                            if (timeDiffMs >= GetMinWaitTime()) // 已经有超时保证上限，只需校验下限
                            {
                                // 校验成功！
                                Log.Information("✅ FIFO匹配成功: 序号={SignalNumber}, 条码={Barcode}, 等待时间={TimeDiff:F0}ms", 
                                      result.Signal.Value, package.Barcode, timeDiffMs);
                                
                                package.Index = result.Signal.Value;
                                package.ProcessingTime = (long)timeDiffMs;
                                
                                AssignChuteToPackage(package);
                                _ = _moduleConnectionService.SendSortingCommandAsync((ushort)package.Index, (byte)package.ChuteNumber);
                                
                                UpdateStatistics(package);
                                UpdatePackageInfoItems_Final(package);
                                PackageHistory.Insert(0, package);
                                if (PackageHistory.Count > 1000) PackageHistory.RemoveAt(PackageHistory.Count - 1);
                                _ = Task.Run(() => SavePackageToHistoryAsync(package));
                            }
                            else
                            {
                                // 匹配到了，但来得太快，视为异常
                                Log.Warning("❌ 匹配对时间无效: 序号={SignalNumber}, 条码={Barcode}, 时间差={TimeDiff:F0}ms 小于最小等待时间", 
                                    result.Signal.Value, package.Barcode, timeDiffMs);
                                
                                package.Index = result.Signal.Value;
                                package.SetStatus("time_mismatch_too_fast");
                                _ = _moduleConnectionService.SendSortingCommandAsync((ushort)package.Index, (byte)GetExceptionChute());

                                UpdateStatistics(package);
                                UpdatePackageInfoItems_Final(package);
                                _ = Task.Run(() => SavePackageToHistoryAsync(package));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                         Log.Error(ex, "处理最终匹配结果时发生错误。信号: {Signal}, 包裹: {Barcode}", result.Signal.Value, result.Package?.Barcode);
                    }
                },
                ex => { Log.Error(ex, "事件处理流发生致命错误"); },
                () => { Log.Information("事件处理流已完成"); }
            );

        // --- 初始化加载 ---
        _ = LoadInitialImportedMappingsAsync();
        ResetStatistics();
    }
    
    public DelegateCommand OpenSettingsCommand { get; }
    public DelegateCommand OpenHistoryCommand { get; }
    public DelegateCommand ImportConfigCommand { get; }
    public DelegateCommand SearchImportedCommand { get; }
    public DelegateCommand ResetStatisticsCommand { get; }
    public SystemStatus SystemStatus
    {
        get => _systemStatus;
        private set => SetProperty(ref _systemStatus, value);
    }

    private void ExecuteOpenSettings()
    {
        _dialogService.ShowDialog("SettingsDialog");
    }

    private void ExecuteOpenHistory()
    {
        _dialogService.ShowDialog(nameof(PackageHistoryDialogView));
    }

    private void ExecuteResetStatistics()
    {
        try
        {
            ResetStatistics();
            HandyControl.Controls.Growl.Success("Statistics have been reset successfully!");
            Log.Information("用户手动重置了统计信息");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "用户重置统计信息时发生错误");
            HandyControl.Controls.Growl.Error("Failed to reset statistics. Please check logs.");
        }
    }

    private async void ExecuteImportConfig()
    {
        var openFileDialog = new OpenFileDialog
        {
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            Title = "选择要导入的配置文件"
        };

        if (openFileDialog.ShowDialog() == true)
        {
            try
            {
                var filePath = openFileDialog.FileName;
                Log.Information("开始导入配置文件: {FilePath}", filePath);

                var newMappings = new List<BarcodeChuteMapping>();

                string[] lines = await File.ReadAllLinesAsync(filePath, Encoding.UTF8);

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var parts = line.Split([','], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        string barcodePart = parts[0].Trim();
                        string chutePart = parts[1].Trim();

                        if (barcodePart.StartsWith('^') && barcodePart.EndsWith('^') &&
                            chutePart.StartsWith('^') && chutePart.EndsWith('^'))
                        {
                            string barcode = barcodePart.Substring(1, barcodePart.Length - 2);
                            string chute = chutePart.Substring(1, chutePart.Length - 2);

                            if (!string.IsNullOrWhiteSpace(barcode) && !string.IsNullOrWhiteSpace(chute))
                            {
                                newMappings.Add(new BarcodeChuteMapping { Barcode = barcode, Chute = chute });
                            }
                        }
                    }
                }

                if (newMappings.Count != 0)
                {
                    var settingsToSave = new BarcodeChuteMappingSettings { Mappings = newMappings };
                    _settingsService.SaveSettings(settingsToSave, true);
                    Log.Information("成功导入并保存 {Count} 条条码格口映射配置。", newMappings.Count);
                    HandyControl.Controls.Growl.Success($"Successfully imported {newMappings.Count} mapping configurations!");

                    AllImportedMappings.Clear();
                    FilteredImportedMappings.Clear();
                    foreach (var mapping in newMappings)
                    {
                        AllImportedMappings.Add(mapping);
                    }
                    ExecuteSearchImported();
                }
                else
                {
                    Log.Information("导入的文件中未找到有效的条码格口映射数据。");
                    HandyControl.Controls.Growl.Warning("No valid mapping data found.");
                    AllImportedMappings.Clear();
                    FilteredImportedMappings.Clear();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "导入配置文件失败。");
                HandyControl.Controls.Growl.Error($"Failed to import configuration file: {ex.Message}");
            }
        }
    }
    
    private void Timer_Tick(object? sender, EventArgs e)
    {
        SystemStatus = SystemStatus.GetCurrentStatus();
    }
    
    public string CurrentBarcode
    {
        get => _currentBarcode;
        private set => SetProperty(ref _currentBarcode, value);
    }

    public string SearchImportedText
    {
        get => _searchImportedText;
        set
        {
            if (SetProperty(ref _searchImportedText, value))
            {
                ExecuteSearchImported();
            }
        }
    }

    private void ExecuteSearchImported()
    {
        FilteredImportedMappings.Clear();
        if (string.IsNullOrWhiteSpace(SearchImportedText))
        {
            foreach (var mapping in AllImportedMappings)
            {
                FilteredImportedMappings.Add(mapping);
            }
        }
        else
        {
            string searchText = SearchImportedText.Trim().ToLowerInvariant();
            foreach (var mapping in AllImportedMappings)
            {
                if (mapping.Barcode.ToLowerInvariant().Contains(searchText, StringComparison.InvariantCultureIgnoreCase) ||
                    mapping.Chute.ToLowerInvariant().Contains(searchText, StringComparison.InvariantCultureIgnoreCase))
                {
                    FilteredImportedMappings.Add(mapping);
                }
            }
        }
    }
    
    private void InitializeDeviceStatuses()
    {
        DeviceStatuses.Add(new DeviceStatus { Name = "Camera Service", Status = "Disconnected", Icon = "Camera24", StatusColor = "#F44336" });
        DeviceStatuses.Add(new DeviceStatus { Name = "Module Belt", Status = "Disconnected", Icon = "ArrowSort24", StatusColor = "#F44336" });
    }
    
    private void InitializeStatisticsItems()
    {
        StatisticsItems.Add(new StatisticsItem("Total Packages", "0", "pcs", "Total number of packages processed", "BoxMultiple24"));
        StatisticsItems.Add(new StatisticsItem("Successes", "0", "pcs", "Number of successfully processed packages", "CheckmarkCircle24"));
        StatisticsItems.Add(new StatisticsItem("Failures", "0", "pcs", "Number of failed packages", "ErrorCircle24"));
        StatisticsItems.Add(new StatisticsItem("Processing Rate", "0", "pcs/hr", "Packages processed per hour", "ArrowTrendingLines24"));
    }
    
    private void InitializePackageInfoItems()
    {
        PackageInfoItems.Add(new PackageInfoItem("Chute", "N/A", "", "Target chute number", "DoorArrowRight24"));
        PackageInfoItems.Add(new PackageInfoItem("Time", "--:--:--", "", "Processing time", "Timer24"));
        PackageInfoItems.Add(new PackageInfoItem("Status", "Waiting", "", "Processing status", "AlertCircle24"));
    }
    
    private void OnModuleConnectionChanged(object? sender, bool isConnected)
    {
        var moduleStatus = DeviceStatuses.FirstOrDefault(static x => x.Name == "Module Belt");
        if (moduleStatus != null)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                moduleStatus.Status = isConnected ? "Connected" : "Disconnected";
                moduleStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
            });
        }
    }
    
    private void OnCameraConnectionChanged(string? cameraId, bool isConnected)
    {
        var cameraStatus = DeviceStatuses.FirstOrDefault(static x => x.Name == "Camera Service");
        if (cameraStatus != null)
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                cameraStatus.Status = isConnected ? "Connected" : "Disconnected";
                cameraStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
            });
        }
    }

    private void UpdateStatistics(PackageInfo package)
    {
        _totalPackages++;
        if (_firstPackageTime == DateTime.MinValue) _firstPackageTime = DateTime.Now;

        bool isSuccess = package.Status switch
        {
            "sorted" => true,
            _ => false
        };

        if (isSuccess) _successPackages++;
        else _failurePackages++;
        
        var elapsedTime = DateTime.Now - _firstPackageTime;
        var rate = elapsedTime.TotalHours > 0 ? _totalPackages / elapsedTime.TotalHours : 0;
        
        StatisticsItems.FirstOrDefault(s => s.Label == "Total Packages")!.Value = _totalPackages.ToString();
        StatisticsItems.FirstOrDefault(s => s.Label == "Successes")!.Value = _successPackages.ToString();
        StatisticsItems.FirstOrDefault(s => s.Label == "Failures")!.Value = _failurePackages.ToString();
        StatisticsItems.FirstOrDefault(s => s.Label == "Processing Rate")!.Value = rate.ToString("F1");
    }

    private void ResetStatistics()
    {
        _totalPackages = 0;
        _successPackages = 0;
        _failurePackages = 0;
        _firstPackageTime = DateTime.MinValue;
        StatisticsItems.FirstOrDefault(s => s.Label == "Total Packages")!.Value = "0";
        StatisticsItems.FirstOrDefault(s => s.Label == "Successes")!.Value = "0";
        StatisticsItems.FirstOrDefault(s => s.Label == "Failures")!.Value = "0";
        StatisticsItems.FirstOrDefault(s => s.Label == "Processing Rate")!.Value = "0";
    }

    private async Task LoadInitialImportedMappingsAsync()
    {
        try
        {
            var savedMappingsSettings = await Task.Run(() => _settingsService.LoadSettings<BarcodeChuteMappingSettings>());
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                AllImportedMappings.Clear();
                if (savedMappingsSettings.Mappings.Count > 0)
                {
                    foreach (var mapping in savedMappingsSettings.Mappings) AllImportedMappings.Add(mapping);
                    ExecuteSearchImported();
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load saved mappings.");
            Application.Current.Dispatcher.Invoke(AllImportedMappings.Clear);
        }
    }

    private async Task SavePackageToHistoryAsync(PackageInfo packageInfo)
    {
        try
        {
            var historyRecord = PackageHistoryRecord.FromPackageInfo(packageInfo);
            await _packageHistoryDataService.AddPackageAsync(historyRecord);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save package {Barcode} to history.", packageInfo.Barcode);
        }
    }
    
    private void UpdatePackageInfoItems_Final(PackageInfo processedPackage)
    {
        PackageInfoItems.FirstOrDefault(i => i.Label == "Chute")!.Value = processedPackage.ChuteNumber.ToString();
        PackageInfoItems.FirstOrDefault(i => i.Label == "Time")!.Value = processedPackage.CreateTime.ToString("HH:mm:ss");
        var statusItem = PackageInfoItems.FirstOrDefault(i => i.Label == "Status");
        if (statusItem != null)
        {
            statusItem.Value = processedPackage.StatusDisplay;
            statusItem.Icon = processedPackage.Status switch
            {
                "sorted" => "CheckmarkCircle24",
                "timeout" or "time_mismatch_too_fast" or "no_rule_found" or "chute_out_of_range" => "ErrorCircle24",
                "no read" or "no_read" => "Warning24",
                _ => "AlertCircle24"
            };
        }
    }

    /// <summary>
    /// 根据业务规则为包裹分配格口 (同步方法)
    /// </summary>
    private void AssignChuteToPackage(PackageInfo packageInfo)
    {
        if (packageInfo.Barcode == "NOREAD" || string.Equals(packageInfo.Status, "no read", StringComparison.OrdinalIgnoreCase))
        {
            packageInfo.SetChute(GetExceptionChute());
            packageInfo.SetStatus("no_read");
            return;
        }

        var mappingSettings = _settingsService.LoadSettings<BarcodeChuteMappingSettings>();
        var moduleTcpSettings = _settingsService.LoadSettings<ModelsTcpSettings>();
        var mapping = mappingSettings.Mappings.FirstOrDefault(m => m.Barcode == packageInfo.Barcode);

        if (mapping != null)
        {
            var chuteSettingsConfig = _settingsService.LoadSettings<ChuteSettings>();
            var chuteData = chuteSettingsConfig.Items.FirstOrDefault(csd =>
                string.Equals(csd.BranchCode.Trim(), mapping.Chute.Trim(), StringComparison.OrdinalIgnoreCase));

            if (chuteData != null && chuteData.SN is > 0 and <= 32)
            {
                packageInfo.SetChute(chuteData.SN);
                packageInfo.SetStatus("sorted");
            }
            else
            {
                packageInfo.SetChute(moduleTcpSettings.NoRuleChute);
                packageInfo.SetStatus("chute_out_of_range");
            }
        }
        else
        {
            packageInfo.SetChute(moduleTcpSettings.NoRuleChute);
            packageInfo.SetStatus("no_rule_found");
        }
    }

    public void Dispose()
    {
        try
        {
            _timer.Stop();
            _timer.Tick -= Timer_Tick;
            _moduleConnectionService.ConnectionStateChanged -= OnModuleConnectionChanged;
            _cameraService.ConnectionChanged -= OnCameraConnectionChanged;
            _packageStreamSubscription?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "释放资源时发生错误");
        }
        GC.SuppressFinalize(this);
    }
    
    // 辅助方法获取配置参数
    private int GetMinWaitTime()
    {
        try
        {
            return _settingsService.LoadSettings<ModelsTcpSettings>().MinWaitTime;
        }
        catch
        {
            Log.Warning("无法加载 MinWaitTime 配置, 使用默认值 100ms");
            return 100; // 默认值
        }
    }

    private int GetMaxWaitTime()
    {
        try
        {
            return _settingsService.LoadSettings<ModelsTcpSettings>().MaxWaitTime;
        }
        catch
        {
            Log.Warning("无法加载 MaxWaitTime 配置, 使用默认值 2000ms");
            return 2000; // 默认值
        }
    }

    private int GetExceptionChute()
    {
        try
        {
            return _settingsService.LoadSettings<ModelsTcpSettings>().ExceptionChute;
        }
        catch
        {
            Log.Warning("无法加载 ExceptionChute 配置, 使用默认值 999");
            return 999; // 默认值
        }
    }
}