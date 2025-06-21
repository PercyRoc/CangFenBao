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
using System.Reactive;
using System.Collections.Concurrent;
using System.Reactive.Disposables;
using System.Linq;
using System.Reactive.Concurrency;

namespace WeiCiModule.ViewModels;

public class MainViewModel : BindableBase, IDisposable
{
    private readonly IDialogService _dialogService;
    private readonly TcpCameraService _cameraService;
    private readonly IModuleConnectionService _moduleConnectionService;
    private readonly ISettingsService _settingsService;
    private readonly IPackageHistoryDataService _packageHistoryDataService;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _cleanupTimer; // 历史数据清理定时器
    private string _currentBarcode = string.Empty;
    private SystemStatus _systemStatus = new();
    private string _searchImportedText = string.Empty;
    
    // 统计信息相关字段
    private int _totalPackages = 0;
    private int _successPackages = 0;
    private int _failurePackages = 0;
    private DateTime _firstPackageTime = DateTime.MinValue;
    
    // 新架构核心成员
    private readonly ConcurrentDictionary<ushort, Timestamped<ushort>> _waitingSignalPool = new();
    private readonly CompositeDisposable _subscriptions = new CompositeDisposable();
    // 添加一个专门用于保护信号池操作的锁对象
    private readonly object _signalPoolLock = new object();

    // 为事件匹配创建一个专用的、隔离的线程，并为其命名
    private readonly EventLoopScheduler _matchingScheduler = new(ts => new Thread(ts) { Name = "PackageMatchingThread" });

    // 为PLC信号处理创建专用线程，确保信号能立即处理
    private readonly EventLoopScheduler _signalScheduler = new(ts => new Thread(ts) { Name = "PLCSignalThread" });

    public ObservableCollection<PackageInfo> PackageHistory { get; } = [];
    
    // 【UI性能优化】使用专门的UI更新队列，避免ObservableCollection阻塞
    private readonly ConcurrentQueue<Action> _uiUpdateQueue = new();
    private readonly Timer _uiUpdateTimer;
    
    // 【数据库性能优化】批量数据库保存机制，避免频繁的单条插入
    private readonly ConcurrentQueue<PackageInfo> _dbSaveQueue = new();
    private readonly Timer _dbBatchSaveTimer;
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

        // 初始化历史数据清理定时器 - 每24小时执行一次清理
        _cleanupTimer = new DispatcherTimer 
        { 
            Interval = TimeSpan.FromHours(24) // 每24小时清理一次
        };
        _cleanupTimer.Tick += CleanupTimer_Tick;
        _cleanupTimer.Start();
        Log.Information("历史数据清理定时器已启动，每24小时自动清理超过3个月的数据");
        
        // 【UI性能优化】初始化UI更新定时器，批量处理UI更新，避免频繁的Dispatcher调用
        _uiUpdateTimer = new Timer(ProcessUIUpdateQueue, null, 50, 50);
        
        // 【数据库性能优化】初始化数据库批量保存定时器，每2秒批量处理一次，大幅减少数据库I/O压力
        _dbBatchSaveTimer = new Timer(ProcessDbSaveQueue, null, 2000, 2000);

        // 初始化UI集合
        InitializeDeviceStatuses();
        InitializeStatisticsItems();
        InitializePackageInfoItems();
        
        CurrentBarcode = "Standby";

        // 订阅外部服务状态变更
        _moduleConnectionService.ConnectionStateChanged += OnModuleConnectionChanged;
        _cameraService.ConnectionChanged += OnCameraConnectionChanged;
        
        // --- 最终的、带线程隔离的响应式架构 ---

        // 1. 获取源头事件流
        var signalStream = _moduleConnectionService.TriggerSignalStream;
        var cameraStream = _cameraService.PackageStream;

        // 2. 订阅PLC信号流 (入口) - 使用专用信号线程
        var signalSubscription = signalStream
            .ObserveOn(_signalScheduler) // 在专用信号线程上处理
            .Subscribe(
                timedSignal =>
                {
                    Log.Debug("🚀 PLCSignalThread收到信号: {SignalNumber} at {Timestamp}", timedSignal.Value, timedSignal.Timestamp);
                    
                    // 在专用信号线程上立即加入信号池
                    lock (_signalPoolLock)
                    {
                        if (_waitingSignalPool.TryAdd(timedSignal.Value, timedSignal))
                        {
                            Log.Debug("信号池: 已添加信号 {SignalNumber}，当前池中数量: {PoolCount}", timedSignal.Value, _waitingSignalPool.Count);
                        }
                        else
                        {
                            Log.Warning("信号池: 信号 {SignalNumber} 已存在，忽略重复信号。", timedSignal.Value);
                            return; // 重复信号直接返回
                        }
                    }

                    // 为这个信号启动一个独立的超时监控（在匹配线程上调度，避免阻塞信号线程）
                    _matchingScheduler.Schedule(TimeSpan.FromMilliseconds(GetMaxWaitTime()), () =>
                    {
                        // 检查信号是否还在池中
                        lock (_signalPoolLock)
                        {
                            if (_waitingSignalPool.TryRemove(timedSignal.Value, out var removedSignal))
                            {
                                // 如果能移除，说明它确实超时了
                                Log.Warning("❌ 信号 {SignalNumber} 在 {MaxWaitTime}ms 内等待包裹超时！(从信号池中移除)", 
                                    removedSignal.Value, GetMaxWaitTime());

                                // 【用户要求】超时的PLC信号不更新UI和不保存数据库，只发送异常口指令和记录日志
                                Log.Information("超时信号 {Signal} 的分拣指令发送至异常口。", removedSignal.Value);
                                
                                // 【终极修复】将PLC网络操作完全异步化，避免阻塞信号处理线程
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        await _moduleConnectionService.SendSortingCommandAsync(removedSignal.Value, (byte)GetExceptionChute());
                                        Log.Debug("超时信号PLC指令发送完成: {Signal}", removedSignal.Value);
                                    }
                                    catch (Exception ex)
                                    {
                                        Log.Error(ex, "超时信号PLC指令发送失败: {Signal}", removedSignal.Value);
                                    }
                                });

                                // 【注释掉UI更新】超时信号不是真实包裹，不需要在界面显示
                                /*
                                // 在UI线程上处理超时逻辑（已注释）
                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    var timeoutPackage = PackageInfo.Create();
                                    timeoutPackage.Index = removedSignal.Value;
                                    timeoutPackage.SetStatus("timeout");
                                    UpdateStatistics(timeoutPackage);
                                    UpdatePackageInfoItems_Final(timeoutPackage);
                                    PackageHistory.Insert(0, timeoutPackage);
                                    if (PackageHistory.Count > 1000) PackageHistory.RemoveAt(PackageHistory.Count - 1);
                                });
                                */
                            }
                        }
                    });
                },
                ex => Log.Error(ex, "处理信号流时发生致命错误。")
            );

        // 3. 订阅相机数据流 (匹配的触发点)
        var cameraSubscription = cameraStream
            .ObserveOn(_matchingScheduler) // 【核心修改】将所有匹配计算都调度到这个专用线程上
            .Subscribe(
                timedPackage =>
                {
                    try
                    {
                        var package = timedPackage.Value;
                        var packageTimestamp = timedPackage.Timestamp;

                        // 【新增监控】记录相机数据流处理的开始时间，用于检测Subject背压
                        var cameraProcessStartTime = DateTimeOffset.UtcNow;
                        var cameraDataAge = (cameraProcessStartTime - packageTimestamp).TotalMilliseconds;
                        
                        if (cameraDataAge > 100)
                        {
                            Log.Warning("⚠️  相机数据流处理延迟异常: {CameraDataAge:F0}ms, 包裹={Barcode}", cameraDataAge, package.Barcode);
                        }

                        Log.Debug("包裹到达: {Barcode} at {Timestamp}。在专用匹配线程上搜索信号池(数量:{PoolCount})...", package.Barcode, packageTimestamp, _waitingSignalPool.Count);
                
                        // 【关键修改】使用锁来保证整个"查找-选择-移除"操作的原子性
                        Timestamped<ushort>? matchedSignal = null;
                        lock (_signalPoolLock)
                        {
                            Log.Debug("🔍 开始匹配搜索：包裹 {Barcode}，池中信号数量: {PoolCount}", package.Barcode, _waitingSignalPool.Count);
                            
                            // 为每个信号记录详细的时间差计算
                            foreach (var kvp in _waitingSignalPool)
                            {
                                var signalNumber = kvp.Key;
                                var signalTimestamp = kvp.Value;
                                var timeDiffMs = (packageTimestamp - signalTimestamp.Timestamp).TotalMilliseconds;
                                var isInRange = timeDiffMs >= GetMinWaitTime() && timeDiffMs <= GetMaxWaitTime();
                                
                                Log.Debug("📊 信号 {SignalNumber}: 时间差={TimeDiff:F0}ms, 范围[{MinWait}-{MaxWait}]ms, 符合条件={IsInRange}", 
                                    signalNumber, timeDiffMs, GetMinWaitTime(), GetMaxWaitTime(), isInRange);
                            }
                            
                            // 筛选出所有时间窗口内符合条件的信号
                            var potentialSignals = _waitingSignalPool.Values
                                .Where(sig =>
                                {
                                    var timeDiffMs = (packageTimestamp - sig.Timestamp).TotalMilliseconds;
                                    return timeDiffMs >= GetMinWaitTime() && timeDiffMs <= GetMaxWaitTime();
                                })
                                .OrderBy(sig => sig.Timestamp) // 按时间戳升序排序
                                .ToList();
                    
                            Log.Debug("🎯 筛选结果：找到 {Count} 个符合条件的信号", potentialSignals.Count);
                            
                            if (potentialSignals.Any())
                            {
                                // 找到了一个或多个！选择最早的那个
                                var bestMatchSignal = potentialSignals.First();
                                var bestTimeDiff = (packageTimestamp - bestMatchSignal.Timestamp).TotalMilliseconds;
                        
                                Log.Debug("✨ 选择最佳匹配：信号 {SignalNumber}，时间差 {TimeDiff:F0}ms", bestMatchSignal.Value, bestTimeDiff);
                        
                                // 【关键原子操作】尝试从池中移除这个信号，确保它只被匹配一次
                                if (_waitingSignalPool.TryRemove(bestMatchSignal.Value, out _))
                                {
                                    // 移除成功！正式建立配对关系
                                    matchedSignal = bestMatchSignal;
                                    Log.Debug("✅ 成功移除信号 {SignalNumber} 从池中", bestMatchSignal.Value);
                                }
                                else
                                {
                                    // 这种情况理论上不应该发生，因为我们在锁内操作
                                    Log.Error("严重错误：在锁内操作时，信号 {SignalNumber} 竟然无法移除！", bestMatchSignal.Value);
                                }
                            }
                            else
                            {
                                Log.Debug("❌ 未找到符合时间窗口的信号");
                            }
                        } // 锁在这里释放
                        
                        // 在锁外处理匹配结果
                        if (matchedSignal.HasValue)
                        {
                            var timeDiffMs = (packageTimestamp - matchedSignal.Value.Timestamp).TotalMilliseconds;
                            Log.Information("✅ 包裹驱动匹配成功: 条码={Barcode} 匹配到信号 {SignalNumber}, 等待时间={TimeDiff:F0}ms", 
                                          package.Barcode, matchedSignal.Value.Value, timeDiffMs);

                            // 【核心修复】职责分离：UI更新 vs 后台I/O
                            // 在当前后台线程(_matchingScheduler)上执行业务逻辑和数据准备
                            package.Index = matchedSignal.Value.Value;
                            package.ProcessingTime = (long)timeDiffMs;
                            AssignChuteToPackage(package);

                            // 【UI性能优化】使用队列批量处理UI更新，避免频繁阻塞UI线程
                            EnqueueUIUpdate(() => 
                            {
                                UpdateStatistics(package);
                                UpdatePackageInfoItems_Final(package);
                                PackageHistory.Insert(0, package);
                                if (PackageHistory.Count > 1000) PackageHistory.RemoveAt(PackageHistory.Count - 1);
                            });

                            // 第2步：【终极修复】真正的Fire-and-Forget模式，专用线程立即释放
                            // 【网络隔离修复】将PLC网络操作完全移到后台线程，避免与TCP相机服务竞争
                            var plcTask = Task.Run(async () => 
                                await _moduleConnectionService.SendSortingCommandAsync((ushort)package.Index, (byte)package.ChuteNumber));
                            
                            // 【数据库性能优化】将包裹加入批量保存队列，避免频繁的单条数据库操作
                            EnqueueDbSave(package);

                            // 【最终修复】为PLC任务添加错误处理
                            plcTask.ContinueWith(t => 
                            {
                                if (t.IsFaulted) 
                                    Log.Error(t.Exception, "发送PLC指令失败: 信号={SignalNumber}, 格口={ChuteNumber}", package.Index, package.ChuteNumber);
                                else 
                                    Log.Debug("PLC指令发送完成: 信号={SignalNumber}, 格口={ChuteNumber}", package.Index, package.ChuteNumber);
                            }); // 🔑 移除ExecuteSynchronously，使用默认的异步调度
                        }
                        else
                        {
                            // 在指定的时间窗口内，一个匹配的信号都没有找到
                            Log.Warning("❌ 无匹配: 包裹 {Barcode} 未在时间窗口内找到任何有效信号。", package.Barcode);
                            
                            // 【用户澄清】无匹配的包裹是真实包裹，需要更新UI和保存数据库
                            package.Index = 0; // 设置默认序号
                            package.SetChute(GetExceptionChute()); // 设置异常格口
                            package.SetStatus("no_signal_match"); // 设置状态为无信号匹配
                            
                            Log.Information("无匹配包裹处理: 条码={Barcode}, 状态={Status}, 异常格口={ChuteNumber}", 
                                package.Barcode, package.Status, package.ChuteNumber);
                            
                            // 【UI性能优化】无匹配包裹的UI更新也使用队列批量处理
                            EnqueueUIUpdate(() => 
                            {
                                CurrentBarcode = package.Barcode;
                                UpdateStatistics(package);
                                UpdatePackageInfoItems_Final(package);
                                PackageHistory.Insert(0, package);
                                if (PackageHistory.Count > 1000) PackageHistory.RemoveAt(PackageHistory.Count - 1);
                            });

                            // 【数据库性能优化】无匹配包裹也使用批量保存队列
                            EnqueueDbSave(package);
                        }
                        
                        // 【新增监控】记录相机数据流处理的总耗时
                        var cameraProcessDuration = (DateTimeOffset.UtcNow - cameraProcessStartTime).TotalMilliseconds;
                        if (cameraProcessDuration > 100)
                        {
                            Log.Warning("⚠️  相机数据流处理总耗时异常: {CameraProcessDuration:F0}ms, 包裹={Barcode}", cameraProcessDuration, package.Barcode);
                        }
                        else
                        {
                            Log.Debug("相机数据流处理完成: 包裹={Barcode}, 总耗时={CameraProcessDuration:F0}ms", package.Barcode, cameraProcessDuration);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "在专用匹配线程上处理相机数据时发生错误。包裹: {Barcode}", timedPackage.Value.Barcode);
                    }
                },
                ex => Log.Error(ex, "处理相机流时发生致命错误。")
            );
        
        // --- 订阅管理 ---
        _subscriptions.Add(signalSubscription);
        _subscriptions.Add(cameraSubscription);
        
        // --- 初始化加载 ---
        _ = LoadInitialImportedMappingsAsync();
        ResetStatistics();
        
        // --- 历史数据清理：只保留最近3个月的数据 ---
        _ = Task.Run(async () =>
        {
            try
            {
                Log.Information("开始清理历史数据，只保留最近3个月的记录...");
                await _packageHistoryDataService.CleanupOldTablesAsync(3);
                Log.Information("历史数据清理完成");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "清理历史数据时发生错误");
            }
        });
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

    private async void CleanupTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            Log.Information("定期历史数据清理开始...");
            await _packageHistoryDataService.CleanupOldTablesAsync(3);
            Log.Information("定期历史数据清理完成，已清理超过3个月的数据");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "定期清理历史数据时发生错误");
        }
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
            Log.Information("MainViewModel 开始释放资源...");
            
            // 1. 停止定时器
            _timer?.Stop();
            if (_timer != null)
            {
                _timer.Tick -= Timer_Tick;
            }
            
            // 停止历史数据清理定时器
            _cleanupTimer?.Stop();
            if (_cleanupTimer != null)
            {
                _cleanupTimer.Tick -= CleanupTimer_Tick;
                Log.Debug("历史数据清理定时器已停止");
            }
            
            // 停止UI更新定时器
            _uiUpdateTimer?.Dispose();
            Log.Debug("UI更新定时器已停止");
            
            // 【数据库性能优化】停止数据库批量保存定时器，并确保所有数据都保存完成
            _dbBatchSaveTimer?.Dispose();
            Log.Debug("数据库批量保存定时器已停止");
            
            // 【关键修复】确保所有待保存的数据都被处理完，避免数据丢失
            if (!_dbSaveQueue.IsEmpty)
            {
                Log.Information("正在保存剩余的 {Count} 条数据库记录...", _dbSaveQueue.Count);
                ProcessDbSaveQueue(null); // 手动触发一次保存
                
                // 等待最后一次保存完成，最多等待5秒
                var waitStart = DateTime.Now;
                while (!_dbSaveQueue.IsEmpty && (DateTime.Now - waitStart).TotalSeconds < 5)
                {
                    System.Threading.Thread.Sleep(100);
                }
                
                if (_dbSaveQueue.IsEmpty)
                {
                    Log.Information("所有数据库记录保存完成");
                }
                else
                {
                    Log.Warning("仍有 {Count} 条数据库记录未保存完成", _dbSaveQueue.Count);
                }
            }
            
            // 2. 取消事件订阅
            _moduleConnectionService.ConnectionStateChanged -= OnModuleConnectionChanged;
            _cameraService.ConnectionChanged -= OnCameraConnectionChanged;
            
            // 3. 释放所有Rx订阅
            _subscriptions.Dispose();
            Log.Debug("Rx订阅已释放");
            
            // 4. 释放调度器（这些可能包含活动线程）
            var schedulerDisposeTask = Task.Run(() =>
            {
                try
                {
                    _matchingScheduler?.Dispose();
                    Log.Debug("包裹匹配调度器已释放");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "释放包裹匹配调度器时发生错误");
                }
                
                try
                {
                    _signalScheduler?.Dispose();
                    Log.Debug("信号处理调度器已释放");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "释放信号处理调度器时发生错误");
                }
            });
            
            // 等待调度器释放，最多等待3秒
            if (!schedulerDisposeTask.Wait(TimeSpan.FromSeconds(3)))
            {
                Log.Warning("调度器释放超时，可能存在顽固线程");
            }
            
            // 5. 清空数据集合
            try
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    PackageHistory.Clear();
                    StatisticsItems.Clear();
                    DeviceStatuses.Clear();
                    PackageInfoItems.Clear();
                    AllImportedMappings.Clear();
                    FilteredImportedMappings.Clear();
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "清空UI集合时发生错误");
            }
            
            Log.Information("MainViewModel 资源释放完成");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "释放MainViewModel资源时发生错误");
        }
        finally
        {
            GC.SuppressFinalize(this);
        }
    }
    
    // 辅助方法获取配置参数
    private int GetMinWaitTime()
    {
        try
        {
            var minWaitTime = _settingsService.LoadSettings<ModelsTcpSettings>().MinWaitTime;
            Log.Verbose("获取MinWaitTime配置: {MinWaitTime}ms", minWaitTime);
            return minWaitTime;
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
            var maxWaitTime = _settingsService.LoadSettings<ModelsTcpSettings>().MaxWaitTime;
            Log.Verbose("获取MaxWaitTime配置: {MaxWaitTime}ms", maxWaitTime);
            return maxWaitTime;
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

    /// <summary>
    /// 【UI性能优化】批量处理UI更新队列，减少Dispatcher调用次数，避免UI线程阻塞
    /// </summary>
    private void ProcessUIUpdateQueue(object? state)
    {
        if (_uiUpdateQueue.IsEmpty)
            return;

        // 【关键优化】批量收集所有待处理的UI更新操作
        var pendingUpdates = new List<Action>();
        
        // 一次性取出队列中的所有操作
        while (_uiUpdateQueue.TryDequeue(out var update))
        {
            pendingUpdates.Add(update);
        }

        if (pendingUpdates.Count == 0)
            return;

        // 【关键修复】只调用一次Dispatcher.InvokeAsync，批量执行所有UI更新
        // 这避免了多次排队到UI线程消息队列，大幅减少UI线程压力
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                // 批量执行所有UI更新操作
                foreach (var update in pendingUpdates)
                {
                    update();
                }
                
                Log.Debug("✅ 批量UI更新完成，本次处理 {Count} 个操作", pendingUpdates.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "批量UI更新时发生错误");
            }
        }, DispatcherPriority.DataBind); // 使用较低优先级，避免干扰用户交互
    }
    
    /// <summary>
    /// 【UI性能优化】将UI更新操作排队，而不是立即执行
    /// </summary>
    private void EnqueueUIUpdate(Action uiUpdate)
    {
        _uiUpdateQueue.Enqueue(uiUpdate);
    }
    
    /// <summary>
    /// 【数据库性能优化】将包裹数据加入批量保存队列
    /// </summary>
    private void EnqueueDbSave(PackageInfo package)
    {
        _dbSaveQueue.Enqueue(package);
        Log.Debug("包裹 {Barcode} 已加入数据库保存队列，当前队列长度: {QueueLength}", package.Barcode, _dbSaveQueue.Count);
    }
    
    /// <summary>
    /// 【数据库性能优化】批量处理数据库保存队列，显著减少磁盘I/O和线程池压力
    /// </summary>
    private void ProcessDbSaveQueue(object? state)
    {
        if (_dbSaveQueue.IsEmpty)
            return;

        // 批量收集所有待保存的包裹
        var packagesToSave = new List<PackageInfo>();
        
        // 一次性取出队列中的所有包裹（最多100个，避免内存过大）
        int batchSize = 0;
        while (_dbSaveQueue.TryDequeue(out var package) && batchSize < 100)
        {
            packagesToSave.Add(package);
            batchSize++;
        }

        if (packagesToSave.Count == 0)
            return;

        // 在后台线程池中执行批量数据库保存
        _ = Task.Run(async () =>
        {
            try
            {
                var startTime = DateTimeOffset.UtcNow;
                await BatchSavePackagesToHistoryAsync(packagesToSave);
                var duration = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
                
                Log.Information("✅ 批量数据库保存完成: {Count} 条记录，耗时 {Duration:F0}ms", packagesToSave.Count, duration);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "批量数据库保存失败: {Count} 条记录", packagesToSave.Count);
                
                // 如果批量保存失败，将包裹重新放回队列（但不要无限重试，避免死循环）
                foreach (var pkg in packagesToSave)
                {
                    _dbSaveQueue.Enqueue(pkg);
                }
            }
        });
    }
    
    /// <summary>
    /// 【数据库性能优化】批量保存多个包裹到历史数据库
    /// </summary>
    private async Task BatchSavePackagesToHistoryAsync(List<PackageInfo> packages)
    {
        if (packages.Count == 0)
            return;
        
        try
        {
            // 转换为历史记录格式
            var historyRecords = packages.Select(PackageHistoryRecord.FromPackageInfo).ToList();
            
            // 【临时实现】使用现有的单个保存方法实现批量保存，后续可优化为真正的批量SQL
            foreach (var record in historyRecords)
            {
                await _packageHistoryDataService.AddPackageAsync(record);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "批量保存包裹到历史数据库失败");
            throw; // 重新抛出异常，让调用方处理重试逻辑
        }
    }
    

}