using System.Collections.Concurrent;
using System.Text;
using System.Timers;
using Common.Models.Package;
using Common.Models.Settings.Sort.PendulumSort;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.TCP;
using Serilog;
using Serilog.Context;
using Timer = System.Timers.Timer;
using System.Threading.Channels;

namespace SortingServices.Pendulum;

/// <summary>
///     摆轮分拣服务基类，提供单光电单摆轮和多光电多摆轮共同的功能
/// </summary>
public abstract class BasePendulumSortService : IPendulumSortService
{
    private readonly ConcurrentDictionary<string, bool> _deviceConnectionStates = new();
    protected readonly ISettingsService _settingsService;
    private readonly Queue<DateTime> _triggerTimes = new();
    protected readonly ConcurrentDictionary<string, PackageInfo> MatchedPackages = new();
    protected readonly ConcurrentDictionary<int, Timer> PackageTimers = new();
    protected readonly ConcurrentDictionary<int, PackageInfo> PendingSortPackages = new();
    protected readonly ConcurrentDictionary<string, PendulumState> PendulumStates = new();
    protected readonly ConcurrentDictionary<string, ProcessingStatus> ProcessingPackages = new();
    protected readonly Timer TimeoutCheckTimer;
    private bool _disposed;
    protected readonly ConcurrentDictionary<string, DateTime> _lastSignalTimes = new(); // 用于存储上次收到信号的时间
    protected CancellationTokenSource? CancellationTokenSource;
    protected bool IsRunningFlag;
    protected TcpClientService? TriggerClient;

    protected BasePendulumSortService(ISettingsService settingsService)
    {
        _settingsService = settingsService;

        // 初始化超时检查定时器
        TimeoutCheckTimer = new Timer(5000); // 5秒检查一次
        TimeoutCheckTimer.Elapsed += CheckTimeoutPackages;
        TimeoutCheckTimer.AutoReset = true;
    }

    public event EventHandler<(string Name, bool Connected)>? DeviceConnectionStatusChanged;

    public event EventHandler<DateTime>? TriggerPhotoelectricSignal;
    public event EventHandler<(string PhotoelectricName, DateTime SignalTime)>? SortingPhotoelectricSignal;

    public abstract Task InitializeAsync(PendulumSortConfig configuration);

    public abstract Task StartAsync();

    public abstract Task StopAsync();

    public bool IsRunning()
    {
        return IsRunningFlag;
    }

    public void ProcessPackage(PackageInfo package)
    {
        // 在应用 LogContext 之前记录接收信息和初始检查
        Log.Information("收到包裹 {Index}|{Barcode}, 准备处理.", package.Index, package.Barcode);

        if (!IsRunningFlag)
        {
            Log.Warning("[包裹{Index}|{Barcode}] 分拣服务未运行，无法处理.", package.Index, package.Barcode);
            return;
        }

        if (IsPackageProcessing(package.Barcode))
        {
            Log.Warning("[包裹{Index}|{Barcode}] 已在处理中，跳过.", package.Index, package.Barcode);
            return;
        }

        // --- 开始应用日志上下文 ---
        var packageContext = $"[包裹{package.Index}|{package.Barcode}]";
        using (LogContext.PushProperty("PackageContext", packageContext))
        {
            Log.Debug("开始处理流程.");

            // 如果包裹已经有关联的触发时间戳
            if (package.TriggerTimestamp != default)
            {
                Log.Debug("已有触发时间戳 {Timestamp:HH:mm:ss.fff}，跳过匹配逻辑.", package.TriggerTimestamp);
            }
            else
            {
                Log.Debug("开始查找匹配的触发时间.");
                DateTime? matchedTriggerTime = null;

                lock (_triggerTimes) // 确保线程安全
                {
                    var currentTime = DateTime.Now;
                    var config = _settingsService.LoadSettings<PendulumSortConfig>();
                    var lowerBound = config.TriggerPhotoelectric.TimeRangeLower;
                    var upperBound = config.TriggerPhotoelectric.TimeRangeUpper;

                    Log.Debug("当前时间: {CurrentTime:HH:mm:ss.fff}, 触发队列 ({Count}) 待匹配. 允许范围: {Lower}-{Upper}ms",
                        currentTime, _triggerTimes.Count, lowerBound, upperBound);

                    if (_triggerTimes.Count != 0)
                        Log.Verbose("触发队列内容: {Times}", // 使用 Verbose 记录详细队列
                            string.Join(", ", _triggerTimes.Select(t =>
                                $"{t:HH:mm:ss.fff}[{(currentTime - t).TotalMilliseconds:F0}ms]")));

                    // 使用流式处理，避免数据丢失
                    var stillValidTimes = new Queue<DateTime>();
                    var found = false;
                    var matchCount = 0;

                    while (_triggerTimes.TryDequeue(out var triggerTime))
                    {
                        var delay = (currentTime - triggerTime).TotalMilliseconds;

                        if (delay > upperBound) // 超过上限，丢弃
                        {
                            Log.Verbose("丢弃过时触发时间 {TriggerTime:HH:mm:ss.fff} (延迟 {Delay:F0}ms > {Upper}ms)",
                                triggerTime, delay, upperBound);
                            continue; // 跳过这个时间戳，不保留
                        }

                        if (delay < lowerBound) // 小于下限，保留供后续匹配
                        {
                            Log.Verbose("保留较新触发时间 {TriggerTime:HH:mm:ss.fff} (延迟 {Delay:F0}ms < {Lower}ms)",
                                triggerTime, delay, lowerBound);
                            stillValidTimes.Enqueue(triggerTime);
                            continue; // 继续检查下一个，因为可能有更早的符合
                        }

                        // 在有效范围内
                        matchCount++;
                        Log.Verbose("发现潜在匹配触发时间 {TriggerTime:HH:mm:ss.fff} (延迟 {Delay:F0}ms)", triggerTime, delay);

                        if (!found) // 如果是第一个找到的匹配项
                        {
                            matchedTriggerTime = triggerTime;
                            found = true;
                            Log.Information("匹配到触发时间 {TriggerTime:HH:mm:ss.fff}，延迟 {Delay:F0}ms", triggerTime, delay);
                            package.ProcessingTime = delay; // 设置处理时间
                            // 不再将此时间戳重新入队，消耗掉这个时间
                        }
                        else // 如果已经找到过匹配项，则将这个也保留
                        {
                            stillValidTimes.Enqueue(triggerTime);
                            Log.Verbose("已找到匹配项，将此时间戳 {TriggerTime:HH:mm:ss.fff} 保留", triggerTime);
                        }
                    } // End while TryDequeue

                    // 将剩余的有效时间放回主队列
                    while (stillValidTimes.TryDequeue(out var validTime))
                    {
                        _triggerTimes.Enqueue(validTime);
                    }

                    if (matchCount > 1)
                        Log.Warning("在时间范围内找到 {MatchCount} 个潜在匹配，建议检查触发时间范围 ({Lower}-{Upper}ms)",
                            matchCount, lowerBound, upperBound);

                    if (_triggerTimes.Count != 0)
                        Log.Debug("重建后的触发队列 ({Count}): {Times}",
                            _triggerTimes.Count,
                            string.Join(", ", _triggerTimes.Select(static t => t.ToString("HH:mm:ss.fff"))));
                    else
                        Log.Debug("重建后的触发队列为空.");
                } // End lock (_triggerTimes)

                // 处理匹配结果
                if (matchedTriggerTime.HasValue)
                {
                    package.SetTriggerTimestamp(matchedTriggerTime.Value);
                }
                else
                {
                    Log.Warning("未找到匹配的触发时间.");
                }
            } // End else (no initial trigger timestamp)

            // 添加到待处理队列
            PendingSortPackages[package.Index] = package;
            // 确保包裹处于待处理状态
            package.SetSortState(PackageSortState.Pending);
            Log.Information("已添加到待处理队列. 目标格口: {TargetChute}", package.ChuteNumber);

            // 创建超时定时器，根据包裹类型绑定不同的处理方法
            var timer = new Timer();
            double timeoutInterval;
            string timeoutReason;
            var photoelectricName = GetPhotoelectricNameBySlot(package.ChuteNumber);

            if (photoelectricName != null)
            {
                // 这是"可分拣包裹"，绑定到新的超时失败处理方法
                timer.Elapsed += (_, _) => HandleSortTimeout(package, photoelectricName);
                
                timeoutInterval = 10000; // 默认 10s
                timeoutReason = "默认值";
                try
                {
                    var photoelectricConfig = GetPhotoelectricConfig(photoelectricName);
                    double upperLimit;
                    if (photoelectricConfig is SortPhotoelectric sortPhotoConfig)
                    {
                        // 多摆轮模式：使用分拣光电的TimeRangeUpper
                        upperLimit = sortPhotoConfig.TimeRangeUpper;
                        timeoutInterval = upperLimit + 500;
                    }
                    else
                    {
                        // 单摆轮模式：使用触发光电的SortingTimeRangeUpper
                        upperLimit = photoelectricConfig.SortingTimeRangeUpper;
                        timeoutInterval = upperLimit + 500;
                    }
                    timeoutReason = $"光电 '{photoelectricName}' 上限 {upperLimit}ms + 500ms";
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "获取光电 '{PhotoelectricName}' 配置失败，使用默认超时.", photoelectricName);
                }
            }
            else
            {
                // 这是"直行包裹"，绑定到专门的直行超时处理方法
                timer.Elapsed += (_, _) => HandleStraightThroughTimeout(package);
                
                var config = _settingsService.LoadSettings<PendulumSortConfig>();
                timeoutInterval = config.StraightThroughTimeout;
                timeoutReason = $"直行包裹 (目标格口: {package.ChuteNumber})";
                Log.Information("包裹为直行包裹，将使用直行超时配置.");
            }

            timer.Interval = timeoutInterval;
            timer.AutoReset = false;
            PackageTimers[package.Index] = timer;
            timer.Start();

            Log.Debug("设置分拣超时时间: {Timeout}ms ({Reason})", timer.Interval, timeoutReason);
        } // --- 日志上下文结束 ---
    }

    public Dictionary<string, bool> GetAllDeviceConnectionStates()
    {
        return new Dictionary<string, bool>(_deviceConnectionStates);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     重新连接设备
    /// </summary>
    protected abstract Task ReconnectAsync();



    /// <summary>
    ///     处理包裹超时
    /// </summary>
    /// <summary>
    /// 处理可分拣包裹的超时失败
    /// </summary>
    private void HandleSortTimeout(PackageInfo package, string photoelectricName)
    {
        var packageContext = $"[包裹{package.Index}|{package.Barcode}]";
        using (LogContext.PushProperty("PackageContext", packageContext))
        {
            // 1. 清理包裹自身的资源
            if (PackageTimers.TryRemove(package.Index, out var timer))
            {
                timer.Dispose();
            }

            // 2. 从待处理队列中移除，并标记为错误
            if (PendingSortPackages.TryRemove(package.Index, out var pkg))
            {
                pkg.SetSortState(PackageSortState.Error);
                Log.Error("【分拣失败-超时】包裹分拣超时，错过目标光电 '{PhotoelectricName}'。该包裹将直行至末端。", photoelectricName);
            }
            else
            {
                // 如果包裹已经不在队列，可能已被正常处理或被其他机制移除，无需再做任何事
                Log.Debug("超时触发，但包裹已不在待处理队列，无需操作。");
                return;
            }

            // 注意：不再执行回正操作
            // - 对于格口3（直行）包裹：摆轮本来就不动作，保持复位状态，无需回正
            // - 对于格口1、2包裹：如果超时说明错过了分拣时机，强制回正可能影响后续正常包裹的分拣
            Log.Information("包裹超时处理完成，摆轮状态保持不变。");
        }
    }

    /// <summary>
    /// 处理直行包裹的超时（正常流程）
    /// </summary>
    private void HandleStraightThroughTimeout(PackageInfo package)
    {
        var packageContext = $"[包裹{package.Index}|{package.Barcode}]";
        using (LogContext.PushProperty("PackageContext", packageContext))
        {
            // 清理定时器
            if (PackageTimers.TryRemove(package.Index, out var timer))
            {
                timer.Dispose();
            }

            // 直行包裹超时是正常流程
            if (PendingSortPackages.TryRemove(package.Index, out var pkg))
            {
                pkg.SetSortState(PackageSortState.Sorted);
                Log.Information("直行包裹超时，视为分拣成功。已从待处理队列移除。");
            }
            else
            {
                Log.Debug("直行包裹超时触发，但包裹已不在待处理队列中。");
            }
        }
    }

    /// <summary>
    ///     根据格口获取对应的分拣光电名称
    /// </summary>
    protected virtual string? GetPhotoelectricNameBySlot(int slot)
    {
        // 基类默认返回null，由子类实现具体逻辑
        return null;
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            Log.Debug("开始释放 BasePendulumSortService 资源...");
            TimeoutCheckTimer.Dispose();
            TriggerClient?.Dispose();

            foreach (var timer in PackageTimers.Values) timer.Dispose();

            PackageTimers.Clear();
            CancellationTokenSource?.Dispose();
            Log.Debug("BasePendulumSortService 托管资源已释放.");
        }

        // 释放非托管资源

        _disposed = true;
    }

    /// <summary>
    ///     检查超时的包裹
    /// </summary>
    private void CheckTimeoutPackages(object? sender, ElapsedEventArgs e)
    {
        if (ProcessingPackages.IsEmpty) return; // 优化：如果没有处理中的包裹，直接返回

        var now = DateTime.Now;
        // 超时时间可以考虑配置化
        var timeoutThreshold = TimeSpan.FromSeconds(30);

        // 使用 ToList() 创建副本以安全地迭代和移除
        var packagesToCheck = ProcessingPackages.ToList();

        foreach (var (barcode, status) in packagesToCheck)
        {
            var elapsed = now - status.StartTime;

            if (elapsed <= timeoutThreshold) continue;
            Log.Warning(
                "包裹 {Barcode} 在光电 {PhotoelectricId} 处理超时 (持续 {ElapsedSeconds:F1}s > {ThresholdSeconds}s)，将强制移除处理状态.",
                barcode, status.PhotoelectricId, elapsed.TotalSeconds, timeoutThreshold.TotalSeconds);
            ProcessingPackages.TryRemove(barcode, out _);
        }
    }

    /// <summary>
    ///     触发设备连接状态变更事件
    /// </summary>
    private void RaiseDeviceConnectionStatusChanged(string deviceName, bool connected)
    {
        try
        {
            Log.Information("设备连接状态变更: {DeviceName} -> {Status}", deviceName, connected ? "已连接" : "已断开");
            DeviceConnectionStatusChanged?.Invoke(this, (deviceName, connected));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "触发 DeviceConnectionStatusChanged 事件时发生错误 for {DeviceName}", deviceName);
        }
    }

    /// <summary>
    ///     更新设备连接状态
    /// </summary>
    protected void UpdateDeviceConnectionState(string deviceName, bool isConnected)
    {
        if (_deviceConnectionStates.TryGetValue(deviceName, out var currentState) && currentState == isConnected)
            return; // 状态未改变，无需操作

        _deviceConnectionStates[deviceName] = isConnected;
        RaiseDeviceConnectionStatusChanged(deviceName, isConnected); // 状态改变，触发事件
    }

    /// <summary>
    ///     将命令字符串转换为字节数组
    /// </summary>
    protected static byte[] GetCommandBytes(string command)
    {
        // 添加回车换行符
        command += "\r\n";
        return Encoding.ASCII.GetBytes(command);
    }

    /// <summary>
    ///     检查包裹是否正在处理
    /// </summary>
    private bool IsPackageProcessing(string barcode)
    {
        return ProcessingPackages.ContainsKey(barcode);
    }

    /// <summary>
    ///     标记包裹为处理中
    /// </summary>
    private void MarkPackageAsProcessing(string barcode, string photoelectricId)
    {
        var status = new ProcessingStatus
        {
            StartTime = DateTime.Now, // IsProcessing 字段似乎冗余，但保留以防万一
            PhotoelectricId = photoelectricId
        };
        // TryAdd 通常比索引器或 AddOrUpdate 略快，如果确定键不存在
        if (!ProcessingPackages.TryAdd(barcode, status))
        {
            // 如果添加失败，说明可能并发冲突，记录警告
            Log.Warning("尝试标记包裹 {Barcode} 为处理中失败 (可能已被标记).", barcode);
        }
        else
        {
            Log.Debug("包裹 {Barcode} 已标记为由光电 {PhotoelectricId} 处理中.", barcode, photoelectricId);
        }
    }

    /// <summary>
    ///     处理触发光电信号
    /// </summary>
    protected void HandleTriggerPhotoelectric(string data)
    {
        var triggerTime = DateTime.Now;
        Log.Debug("收到触发信号: {Signal}，记录触发时间: {TriggerTime:HH:mm:ss.fff}", data, triggerTime);

        // 触发光电信号事件
        try
        {
            TriggerPhotoelectricSignal?.Invoke(this, triggerTime);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "触发光电信号事件时发生错误");
        }

        lock (_triggerTimes)
        {
            _triggerTimes.Enqueue(triggerTime);
            Log.Verbose("触发时间已入队，当前队列长度: {Count}", _triggerTimes.Count);

            const int maxQueueSize = 5; // 定义最大队列长度常量
            while (_triggerTimes.Count > maxQueueSize)
            {
                var removed = _triggerTimes.Dequeue();
                Log.Warning("触发时间队列超过 {MaxSize} 个，移除最早的时间戳: {RemovedTime:HH:mm:ss.fff}", maxQueueSize, removed);
            }

            if (_triggerTimes.Count != 0)
                Log.Verbose("当前触发时间队列: {Times}",
                    string.Join(", ", _triggerTimes.Select(static t => t.ToString("HH:mm:ss.fff"))));
        }
    }

    /// <summary>
    ///     处理第二光电信号，由子类实现具体逻辑
    /// </summary>
    protected abstract void HandleSecondPhotoelectric(string data);

    /// <summary>
    ///     处理光电信号
    /// </summary>
    protected void HandlePhotoelectricSignal(string data, string photoelectricName)
    {
        var lines = data.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var now = DateTime.Now;

        // 获取全局防抖时间
        var config = _settingsService.LoadSettings<PendulumSortConfig>();
        var debounceTime = config.GlobalDebounceTime;

        foreach (var line in lines)
        {
            // 检查防抖
            if (_lastSignalTimes.TryGetValue(photoelectricName, out var lastSignalTime))
            {
                var elapsedSinceLastSignal = (now - lastSignalTime).TotalMilliseconds;
                if (elapsedSinceLastSignal < debounceTime)
                {
                    Log.Debug("光电 {PhotoelectricName} 在 {DebounceTime}ms 防抖时间内收到重复信号 '{SignalLine}'，已忽略.",
                        photoelectricName, debounceTime, line);
                    continue; // 忽略此信号
                }
            }
            _lastSignalTimes[photoelectricName] = now; // 更新上次信号时间

            Log.Verbose("处理光电信号行: {SignalLine}", line); // 使用 Verbose 记录原始信号行
            // 简化逻辑：直接检查是否包含特定触发或分拣标识
            if (line.Contains("OCCH1:1"))
            {
                HandleTriggerPhotoelectric(line);
            }
            else if (line.Contains("OCCH2:1"))
            {
                HandleSecondPhotoelectric(line);
            }
        }
    }

    /// <summary>
    ///     处理分拣信号并匹配包裹
    /// </summary>
    protected PackageInfo? MatchPackageForSorting(string photoelectricName)
    {
        Log.Debug("分拣光电 {Name} 触发，开始匹配包裹...", photoelectricName);
        
        // 检查摆轮是否处于回正延迟状态，如果是则忽略此信号
        if (PendulumStates.TryGetValue(photoelectricName, out var pendulumState) && 
            pendulumState.CurrentDirection == PendulumDirection.Resetting)
        {
            Log.Debug("光电 {Name} 的摆轮正在回正延迟中，忽略分拣信号", photoelectricName);
            return null;
        }
        
        var currentTime = DateTime.Now;
        PackageInfo? matchedPackage = null;

        try
        {
            // 移除所有关于"幽灵包裹"和"强制同步"的逻辑
            // 新策略：超时包裹已在HandlePackageTimeout中被移除，不再影响后续分拣

            // 正常的包裹匹配逻辑
            var photoelectricConfigBase = GetPhotoelectricConfig(photoelectricName);
            double timeRangeLower, timeRangeUpper;
            if (photoelectricConfigBase is SortPhotoelectric sortPhotoConfig)
            {
                timeRangeLower = sortPhotoConfig.TimeRangeLower;
                timeRangeUpper = sortPhotoConfig.TimeRangeUpper;
            }
            else
            {
                timeRangeLower = photoelectricConfigBase.SortingTimeRangeLower;
                timeRangeUpper = photoelectricConfigBase.SortingTimeRangeUpper;
            }
            
            Log.Information("开始遍历 {Count} 个待处理包裹进行匹配...", PendingSortPackages.Count);
            
            foreach (var pkg in PendingSortPackages.Values.OrderBy(p => p.Index)) // 仍按 Index 排序保证顺序
            {
                // --- 开始应用日志上下文 ---
                var packageContext = $"[包裹{pkg.Index}|{pkg.Barcode}]";
                using (LogContext.PushProperty("PackageContext", packageContext))
                {
                    Log.Information("🔍 检查包裹匹配条件 - 目标格口: {Chute}, 触发时间: {Timestamp:HH:mm:ss.fff}, 分拣状态: {SortState}", 
                        pkg.ChuteNumber, pkg.TriggerTimestamp, pkg.SortState);

                    // 基本条件检查
                    if (pkg.TriggerTimestamp == default)
                    {
                        Log.Information("❌ 匹配失败: 无触发时间戳");
                        continue;
                    }

                    // 只处理待处理状态的包裹，跳过其他状态 (Error, Sorted, TimedOut等)
                    // TimedOut 状态理论上不会再出现，但保留检查以增强鲁棒性
                    if (pkg.SortState != PackageSortState.Pending)
                    {
                        Log.Information("❌ 匹配失败: 包裹状态为 {SortState}，跳过", pkg.SortState);
                        continue;
                    }

                    var slotMatches = SlotBelongsToPhotoelectric(pkg.ChuteNumber, photoelectricName);
                    if (!slotMatches)
                    {
                        Log.Information("❌ 匹配失败: 格口 {Chute} 不属于光电 {PhotoelectricName}", pkg.ChuteNumber, photoelectricName);
                        continue;
                    }

                    if (IsPackageProcessing(pkg.Barcode))
                    {
                        Log.Information("❌ 匹配失败: 包裹已标记为处理中");
                        continue;
                    } // 重要：防止重复处理

                    // 检查是否已超时 (基于 Timer 状态)
                    if (PackageTimers.TryGetValue(pkg.Index, out var timer) && !timer.Enabled)
                    {
                        Log.Information("❌ 匹配失败: 包裹已超时 (Timer 已禁用)");
                        continue;
                    }

                    var delay = (currentTime - pkg.TriggerTimestamp).TotalMilliseconds;
                    const double tolerance = 10.0;
                    var delayInRange = delay >= timeRangeLower - tolerance && delay <= timeRangeUpper + tolerance;
                    
                    Log.Information("⏱️ 时间差计算: 当前时间 {CurrentTime:HH:mm:ss.fff} - 触发时间 {TriggerTime:HH:mm:ss.fff} = {Delay:F1}ms", 
                        currentTime, pkg.TriggerTimestamp, delay);
                    Log.Information("📏 时间范围检查: 延迟 {Delay:F1}ms, 允许范围 [{Lower:F1} - {Upper:F1}]ms (含容差 ±{Tolerance}ms), 结果: {InRange}", 
                        delay, timeRangeLower, timeRangeUpper, tolerance, delayInRange ? "✅ 符合" : "❌ 超出");
                    
                    if (!delayInRange)
                    {
                        Log.Information("❌ 匹配失败: 时间延迟超出允许范围");
                        continue;
                    }

                    // 所有条件都满足，匹配成功
                    Log.Information("🎯 匹配成功! 格口: {Chute}, 延迟: {Delay:F1}ms, 光电: {PhotoelectricName}", 
                        pkg.ChuteNumber, delay, photoelectricName);
                    
                    // 标记为处理中，防止被其他光电或线程重复处理
                    MarkPackageAsProcessing(pkg.Barcode, photoelectricName);
                    // 更新分拣状态为处理中
                    pkg.SetSortState(PackageSortState.Processing);
                    matchedPackage = pkg;
                    break; // 找到第一个匹配的就跳出循环
                } // --- 日志上下文结束 ---
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "匹配包裹时发生异常 (光电: {PhotoelectricName}).", photoelectricName);
            return null; // 发生异常时返回 null
        }

        if (matchedPackage != null)
        {
            // 停止并移除对应的超时定时器
            if (!PackageTimers.TryRemove(matchedPackage.Index, out var timer)) return matchedPackage;
            timer.Stop();
            timer.Dispose();
            Log.Debug("[包裹{Index}|{Barcode}] 匹配成功，已停止并移除超时定时器.", matchedPackage.Index, matchedPackage.Barcode);
        }
        else
        {
            Log.Debug("分拣光电 {Name}: 未找到符合条件的待处理包裹.", photoelectricName);
        }

        return matchedPackage;
    }

    /// <summary>
    ///     获取分拣光电配置
    /// </summary>
    protected virtual TriggerPhotoelectric GetPhotoelectricConfig(string photoelectricName)
    {
        // 尝试从分拣光电配置中查找
        var sortConfig = _settingsService.LoadSettings<PendulumSortConfig>();
        var photoelectricConfig = sortConfig.SortingPhotoelectrics.FirstOrDefault(p => p.Name == photoelectricName);

        if (photoelectricConfig != null)
        {
            return photoelectricConfig;
        }

        // 如果在分拣光电中找不到，检查是否为触发光电 (适用于单摆轮)
        if (photoelectricName is "触发光电" or "默认")
        {
            return sortConfig.TriggerPhotoelectric;
        }

        // 都找不到则抛出异常
        throw new KeyNotFoundException($"无法找到名为 '{photoelectricName}' 的光电配置.");
    }

    /// <summary>
    ///     判断格口是否属于指定的分拣光电
    /// </summary>
    protected virtual bool SlotBelongsToPhotoelectric(int targetSlot, string photoelectricName)
    {
        return true; // 基类默认返回true，由子类实现具体逻辑
    }

    /// <summary>
    ///     执行分拣动作
    /// </summary>
    protected async Task ExecuteSortingAction(PackageInfo package, string photoelectricName)
    {
        // --- 开始应用日志上下文 ---
        var packageContext = $"[包裹{package.Index}|{package.Barcode}]";
        using (LogContext.PushProperty("PackageContext", packageContext))
        {
            Log.Information("开始执行分拣动作 (光电: {PhotoelectricName}, 格口: {Chute}).", photoelectricName, package.ChuteNumber);
            TcpClientService? client;
            PendulumState? pendulumState = null;

            try
            {
                client = GetSortingClient(photoelectricName);
                if (client == null || !client.IsConnected())
                {
                    Log.Warning("分拣客户端 '{Name}' 未连接或未找到，无法执行分拣.", photoelectricName);
                    ProcessingPackages.TryRemove(package.Barcode, out _);
                    return;
                }

                if (!PendulumStates.TryGetValue(photoelectricName, out pendulumState))
                {
                    Log.Error("无法找到光电 '{Name}' 的摆轮状态.", photoelectricName);
                    ProcessingPackages.TryRemove(package.Barcode, out _);
                    return;
                }

                var photoelectricConfig = GetPhotoelectricConfig(photoelectricName);

                // 等待分拣延迟到达最佳位置
                var sortDelay = photoelectricConfig.SortingDelay;
                var actualDelay = sortDelay > 0 ? sortDelay : 50; // 如果延迟为0，固定等待50ms
                Log.Debug("等待分拣延迟: {SortDelay}ms (实际: {ActualDelay}ms)", sortDelay, actualDelay);
                await Task.Delay(actualDelay);

                // 3. 确定目标动作并发送摆动命令
                var targetSlot = package.ChuteNumber;
                var swingLeft = ShouldSwingLeft(targetSlot);
                var swingRight = ShouldSwingRight(targetSlot);
                var needsResetLater = false;

                if (swingLeft || swingRight) // 包裹需要摆动
                {
                    var commandToSend = swingLeft ? PendulumCommands.Module2.SwingLeft : PendulumCommands.Module2.SwingRight;
                    var commandLogName = swingLeft ? "左摆" : "右摆";
                    needsResetLater = true;

                    Log.Debug("发送摆动命令: {CommandLogName} ({CommandToSend})", commandLogName, commandToSend);
                    var commandBytes = GetCommandBytes(commandToSend);
                    
                    if (!await SendCommandWithRetryAsync(client, commandBytes, photoelectricName))
                    {
                        Log.Error("发送摆动命令 '{CommandLogName}' ({CommandToSend}) 失败", commandLogName, commandToSend);
                        ProcessingPackages.TryRemove(package.Barcode, out _);
                        pendulumState.ForceReset(); // 发送失败时强制复位状态
                        return;
                    }

                    // 命令发送成功，更新状态
                    pendulumState.SetSwinging(swingLeft);
                    Log.Information("已发送摆动命令: {CommandLogName} ({CommandToSend}) 并更新状态为: {State}", 
                        commandLogName, commandToSend, pendulumState.GetCurrentState());
                }
                else
                {
                    // 不需要摆动，包裹直行，摆轮保持复位状态
                    Log.Debug("包裹无需摆动，摆轮保持复位状态");
                }

                                PendulumState.UpdateLastSlot(targetSlot);

                // 4. 如果需要，执行延迟回正
                if (needsResetLater)
                {
                    // 需要执行回正，先进行回正延迟
                    var resetDelay = photoelectricConfig.ResetDelay;
                    Log.Debug("开始回正延迟等待 {ResetDelay}ms，期间将忽略新的分拣信号...", resetDelay);

                    // 标记摆轮进入回正延迟状态，阻止新信号处理
                    pendulumState.SetResetting();

                    try
                    {
                        // 完整执行回正延迟，不可中断
                        await Task.Delay(resetDelay);
                        Log.Debug("回正延迟正常结束，开始执行回正");

                        // 执行回正逻辑
                        await ExecuteDelayedReset(client, pendulumState, photoelectricName);
                    }
                    finally
                    {
                        // 确保状态被正确重置，允许后续信号处理
                        if (pendulumState.CurrentDirection == PendulumDirection.Resetting)
                        {
                            pendulumState.SetReset();
                        }
                    }
                }

                // 从待处理队列中移除包裹
                if (PendingSortPackages.TryRemove(package.Index, out _))
                {
                    Log.Debug("分拣动作完成，已从待处理队列移除. {NeedsReset}", 
                        needsResetLater ? "已处理回正" : "无需回正");
                }
                else
                {
                    Log.Warning("尝试移除已完成的包裹失败 (可能已被移除).");
                }

                // 设置包裹分拣状态为已分拣
                package.SetSortState(PackageSortState.Sorted);
                Log.Debug("包裹分拣状态已更新为: Sorted");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "执行分拣动作时发生异常.");
                PendingSortPackages.TryRemove(package.Index, out _);
                
                // 【修复】异常时尝试发送物理回正命令，而不仅仅是软件复位
                if (pendulumState != null)
                {
                    Log.Warning("由于异常，将尝试发送物理回正命令以确保摆轮状态正确");
                    
                    // 在后台线程执行回正，避免阻塞异常处理
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var exceptionClient = GetSortingClient(photoelectricName);
                            if (exceptionClient != null && exceptionClient.IsConnected() && pendulumState.CurrentDirection != PendulumDirection.Reset)
                            {
                                await ExecuteImmediateReset(exceptionClient, pendulumState, photoelectricName, "ExceptionRecovery");
                            }
                            else
                            {
                                pendulumState.ForceReset(); // 如果无法发送命令，至少软件复位
                            }
                        }
                        catch (Exception resetEx)
                        {
                            Log.Error(resetEx, "异常恢复时执行回正操作失败");
                            pendulumState.ForceReset(); // 最后的兜底操作
                        }
                    });
                }
                
                // 异常情况下也要更新状态，避免包裹卡在Processing状态
                package.SetSortState(PackageSortState.Error);
                Log.Error("【分拣失败-异常】包裹 {Index}|{Barcode} 在执行分拣动作时发生异常，状态已设为Error.", package.Index, package.Barcode);
            }
            finally
            {
                if (ProcessingPackages.TryRemove(package.Barcode, out _))
                {
                    Log.Debug("已从处理中状态移除.");
                }
                else
                {
                    Log.Warning("尝试从处理中状态移除失败 (可能已被移除).");
                }
            }
        }
    }







    /// <summary>
    ///     获取用于执行分拣动作的客户端
    /// </summary>
    protected virtual TcpClientService? GetSortingClient(string photoelectricName)
    {
        return TriggerClient; // 基类默认返回触发光电客户端，子类可以重写此方法
    }

    /// <summary>
    ///     判断是否需要向左摆动
    /// </summary>
    private static bool ShouldSwingLeft(int targetSlot)
    {
        // 奇数格口向左摆动
        return targetSlot % 2 == 1;
    }

    /// <summary>
    ///     判断是否需要向右摆动
    /// </summary>
    private static bool ShouldSwingRight(int targetSlot)
    {
        // 偶数格口向右摆动
        return targetSlot % 2 == 0;
    }

    /// <summary>
    ///     包裹处理状态类
    /// </summary>
    protected class ProcessingStatus
    {
        public DateTime StartTime { get; init; }
        public string PhotoelectricId { get; init; } = string.Empty;
    }

    /// <summary>
    ///     摆轮命令结构体
    /// </summary>
    protected readonly struct PendulumCommands
    {
        // 二代模块命令，使用静态属性
        public static PendulumCommands Module2
        {
            get => new()
            {
                Start = "AT+STACH1=1",
                Stop = "AT+STACH1=0",
                SwingLeft = "AT+STACH2=1",
                ResetLeft = "AT+STACH2=0",
                SwingRight = "AT+STACH3=1",
                ResetRight = "AT+STACH3=0"
            };
        }

        public string Start { get; private init; }
        public string Stop { get; private init; }
        public string SwingLeft { get; private init; }
        public string ResetLeft { get; private init; }
        public string SwingRight { get; private init; }
        public string ResetRight { get; private init; }
    }

    /// <summary>
    /// 摆轮方向枚举
    /// </summary>
    protected enum PendulumDirection
    {
        Reset,          // 复位状态
        SwingingLeft,   // 左摆状态
        SwingingRight,  // 右摆状态
        Resetting       // 回正延迟中（阻止新信号处理）
    }

    /// <summary>
    /// 摆轮状态类
    /// </summary>
    protected class PendulumState
    {
        /// <summary>
        /// 获取当前摆轮方向
        /// </summary>
        public PendulumDirection CurrentDirection { get; private set; } = PendulumDirection.Reset;

        /// <summary>
        /// 进入回正延迟状态前的上一个摆轮方向，用于确定正确的回正命令
        /// </summary>
        public PendulumDirection PreviousDirection { get; private set; } = PendulumDirection.Reset;

        /// <summary>
        /// 设置摆动状态
        /// </summary>
        /// <param name="swingLeft">true表示左摆，false表示右摆</param>
        public void SetSwinging(bool swingLeft)
        {
            PreviousDirection = CurrentDirection; // 保存之前的状态
            CurrentDirection = swingLeft ? PendulumDirection.SwingingLeft : PendulumDirection.SwingingRight;
            Log.Debug("摆轮状态更新为: {Direction}", CurrentDirection);
        }

        /// <summary>
        /// 设置复位状态
        /// </summary>
        public void SetReset()
        {
            PreviousDirection = CurrentDirection; // 保存之前的状态
            CurrentDirection = PendulumDirection.Reset;
            Log.Debug("摆轮状态更新为: Reset");
        }

        /// <summary>
        /// 设置回正延迟状态，并保存当前摆轮方向用于后续回正
        /// </summary>
        public void SetResetting()
        {
            // 只有在非Resetting状态时才更新PreviousDirection，避免重复设置
            if (CurrentDirection != PendulumDirection.Resetting)
            {
                PreviousDirection = CurrentDirection;
                Log.Debug("保存摆轮方向 {PreviousDirection} 用于回正", PreviousDirection);
            }
            CurrentDirection = PendulumDirection.Resetting;
            Log.Debug("摆轮状态更新为: Resetting (回正延迟中)");
        }

        /// <summary>
        /// 强制设置复位状态
        /// </summary>
        public void ForceReset()
        {
            PreviousDirection = CurrentDirection; // 保存之前的状态
            CurrentDirection = PendulumDirection.Reset;
            Log.Debug("摆轮状态被强制复位");
        }

        /// <summary>
        /// 获取需要回正的方向（用于确定回正命令）
        /// </summary>
        /// <returns>需要回正的摆轮方向</returns>
        public PendulumDirection GetDirectionForReset()
        {
            // 如果当前是Resetting状态，使用之前保存的方向
            // 否则使用当前方向
            return CurrentDirection == PendulumDirection.Resetting ? PreviousDirection : CurrentDirection;
        }

        /// <summary>
        /// 更新最后处理的格口号
        /// </summary>
        public static void UpdateLastSlot(int slot)
        {
            Log.Debug("更新最后处理的格口为: {Slot}", slot);
        }

        /// <summary>
        /// 获取当前状态的字符串表示
        /// </summary>
        public string GetCurrentState()
        {
            return CurrentDirection switch
            {
                PendulumDirection.Reset => "Reset",
                PendulumDirection.SwingingLeft => "SwingingLeft",
                PendulumDirection.SwingingRight => "SwingingRight",
                PendulumDirection.Resetting => "Resetting",
                _ => "Unknown"
            };
        }
    }

    /// <summary>
    ///     发送命令并重试
    /// </summary>
    private async Task<bool> SendCommandWithRetryAsync(TcpClientService client, byte[] command,
        string photoelectricName, int maxRetries = 3)
    {
        var commandString = Encoding.ASCII.GetString(command).Trim(); // 用于日志记录
        Log.Debug("准备向 {Name} 发送命令: {Command}", photoelectricName, commandString);

        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                if (!client.IsConnected())
                {
                    Log.Warning("客户端 {Name} 未连接 (尝试次数 {Attempt}/{MaxRetries}). 尝试重连...", photoelectricName, i + 1,
                        maxRetries);
                    await ReconnectAsync(); // 应该只重连这个 specific client
                    await Task.Delay(1000); // 等待重连
                    continue; // 继续下一次尝试
                }

                client.Send(command);
                Log.Debug("命令 {Command} 已成功发送到 {Name}.", commandString, photoelectricName);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "发送命令 {Command} 到 {Name} 失败 (尝试次数 {Attempt}/{MaxRetries}).", commandString,
                    photoelectricName, i + 1, maxRetries);
                if (i < maxRetries - 1)
                {
                    await Task.Delay(500); // 缩短重试间隔
                }
                else // 最后一次尝试失败
                {
                    Log.Error("发送命令 {Command} 到 {Name} 失败次数达到上限.", commandString, photoelectricName);
                    // 考虑更新设备状态为错误？
                    UpdateDeviceConnectionState(photoelectricName, false); // 标记为断开
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 执行延迟回正
    /// </summary>
    private async Task ExecuteDelayedReset(TcpClientService? client, PendulumState pendulumState, string photoelectricName)
    {
        Log.Debug("执行延迟回正.");
        
        if (client == null || !client.IsConnected())
        {
            Log.Warning("延迟回正时客户端 '{Name}' 未连接.", photoelectricName);
            pendulumState.ForceReset();
            return;
        }

        // 检查回正方向，避免重复发送回正命令
        var directionForReset = pendulumState.GetDirectionForReset();
        if (directionForReset == PendulumDirection.Reset)
        {
            Log.Debug("摆轮回正方向为Reset状态，无需发送延迟回正命令");
            return;
        }

        // 执行回正，使用保存的回正方向
        var resetCommand = directionForReset == PendulumDirection.SwingingLeft
            ? PendulumCommands.Module2.ResetLeft
            : PendulumCommands.Module2.ResetRight;
        var resetCmdBytes = GetCommandBytes(resetCommand);
        var resetDir = directionForReset == PendulumDirection.SwingingLeft ? "左" : "右";

        Log.Debug("根据保存的摆轮方向 {DirectionForReset} 确定回正方向为: {ResetDir}", directionForReset, resetDir);

        Log.Debug("准备发送延迟 {ResetDir} 回正命令 ({ResetCommand})...", resetDir, resetCommand);
        if (await SendCommandWithRetryAsync(client, resetCmdBytes, photoelectricName))
        {
            Log.Information("延迟 {ResetDir} 回正命令 ({ResetCommand}) 发送成功.", resetDir, resetCommand);
            pendulumState.SetReset();
        }
        else
        {
            Log.Error("发送延迟 {ResetDir} 回正命令 ({ResetCommand}) 失败，强制复位状态.", resetDir, resetCommand);
            pendulumState.ForceReset();
        }
    }



    /// <summary>
    /// 执行立即回正（用于强制同步）
    /// </summary>
    private async Task ExecuteImmediateReset(TcpClientService client, PendulumState pendulumState, string photoelectricName, string reason)
    {
        Log.Information("执行立即回正 (原因: {Reason}, 光电: {Name})", reason, photoelectricName);
        
        if (!client.IsConnected())
        {
            Log.Warning("立即回正时客户端 '{Name}' 未连接", photoelectricName);
            pendulumState.ForceReset();
            return;
        }

        try
        {
            // 检查回正方向，避免重复发送回正命令
            var directionForReset = pendulumState.GetDirectionForReset();
            if (directionForReset == PendulumDirection.Reset)
            {
                Log.Debug("摆轮回正方向为Reset状态，无需发送立即回正命令");
                return;
            }

            // 根据保存的摆轮方向发送对应的回正命令
            var resetCommand = directionForReset == PendulumDirection.SwingingLeft
                ? PendulumCommands.Module2.ResetLeft
                : PendulumCommands.Module2.ResetRight;
            
            var commandBytes = GetCommandBytes(resetCommand);
            var resetDirection = directionForReset == PendulumDirection.SwingingLeft ? "左" : "右";

            Log.Debug("根据保存的摆轮方向 {DirectionForReset} 确定立即回正方向为: {ResetDirection}", directionForReset, resetDirection);

            Log.Debug("发送 {Direction} 回正命令 ({Command})...", resetDirection, resetCommand);
            
            if (await SendCommandWithRetryAsync(client, commandBytes, photoelectricName, maxRetries: 2))
            {
                pendulumState.SetReset();
                Log.Information("立即 {Direction} 回正命令发送成功 (光电: {Name})", resetDirection, photoelectricName);
            }
            else
            {
                Log.Error("发送立即 {Direction} 回正命令失败，强制复位状态 (光电: {Name})", resetDirection, photoelectricName);
                pendulumState.ForceReset();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "执行立即回正时发生错误 (光电: {Name})", photoelectricName);
            pendulumState.ForceReset();
        }
    }

    /// <summary>
    ///     触发分拣光电信号事件
    /// </summary>
    protected void RaiseSortingPhotoelectricSignal(string photoelectricName, DateTime signalTime)
    {
        try
        {
            SortingPhotoelectricSignal?.Invoke(this, (photoelectricName, signalTime));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "触发分拣光电信号事件时发生错误，光电: {PhotoelectricName}", photoelectricName);
        }
    }
}