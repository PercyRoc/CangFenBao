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
    private readonly ISettingsService _settingsService;
    private readonly Queue<DateTime> _triggerTimes = new();
    protected readonly ConcurrentDictionary<string, PackageInfo> MatchedPackages = new();
    protected readonly ConcurrentDictionary<int, Timer> PackageTimers = new();
    protected readonly ConcurrentDictionary<int, PackageInfo> PendingSortPackages = new();
    protected readonly ConcurrentDictionary<string, PendulumState> PendulumStates = new();
    protected readonly ConcurrentDictionary<string, ProcessingStatus> ProcessingPackages = new();
    protected readonly Timer TimeoutCheckTimer;
    private bool _disposed;
    private readonly ConcurrentDictionary<string, DateTime> _lastSignalTimes = new(); // 用于存储上次收到信号的时间
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

            // 创建超时定时器
            var timer = new Timer();
            timer.Elapsed += (_, _) => HandlePackageTimeout(package);

            double timeoutInterval = 10000; // 默认 10s
            var timeoutReason = "默认值";
            var photoelectricName = GetPhotoelectricNameBySlot(package.ChuteNumber);

            if (photoelectricName != null)
            {
                try
                {
                    var photoelectricConfig = GetPhotoelectricConfig(photoelectricName);
                    if (photoelectricConfig is SortPhotoelectric)
                    {
                        timeoutInterval = photoelectricConfig.TimeRangeUpper + 500;
                    }
                    else
                    {
                        timeoutInterval = photoelectricConfig.SortingTimeRangeUpper + 500;
                    }
                    timeoutReason = $"光电 '{photoelectricName}' 上限 {photoelectricConfig.TimeRangeUpper}ms + 500ms";
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "获取光电 '{PhotoelectricName}' 配置失败，使用默认超时.", photoelectricName);
                }
            }
            else
            {
                Log.Warning("无法确定目标光电名称 (格口: {Chute}), 使用默认超时 {DefaultTimeout}ms",
                    package.ChuteNumber, timeoutInterval);
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
    ///     尝试获取指定光电的动作队列写入器
    /// </summary>
    /// <param name="photoelectricName">光电名称</param>
    /// <param name="writer">动作队列写入器</param>
    /// <returns>是否成功获取</returns>
    protected abstract bool TryGetActionChannel(string photoelectricName, out ChannelWriter<Func<Task>>? writer);

    /// <summary>
    ///     注册等待任务，用于可中断延迟
    /// </summary>
    /// <param name="photoelectricName">光电名称</param>
    /// <param name="tcs">任务完成源</param>
    protected abstract void RegisterWaitingTask(string photoelectricName, TaskCompletionSource<PackageInfo> tcs);

    /// <summary>
    ///     取消注册等待任务
    /// </summary>
    /// <param name="photoelectricName">光电名称</param>
    protected abstract void UnregisterWaitingTask(string photoelectricName);

    /// <summary>
    ///     处理包裹超时
    /// </summary>
    private void HandlePackageTimeout(PackageInfo package)
    {
        // --- 开始应用日志上下文 ---
        var packageContext = $"[包裹{package.Index}|{package.Barcode}]";
        using (LogContext.PushProperty("PackageContext", packageContext))
        {
            if (PackageTimers.TryRemove(package.Index, out var timer))
            {
                timer.Dispose();
            }

            if (PendingSortPackages.TryGetValue(package.Index, out var pkg))
            {
                // 不再立即移除包裹，而是标记为超时状态，创建"幽灵包裹"
                pkg.SetSortState(PackageSortState.TimedOut);
                Log.Warning("分拣超时，标记为TimedOut状态（幽灵包裹），等待强制同步处理.");
            }
            else
            {
                Log.Debug("超时触发，但包裹已不在待处理队列中 (可能已处理).");
            }
        } // --- 日志上下文结束 ---
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
        var currentTime = DateTime.Now;
        PackageInfo? matchedPackage = null;

        try
        {
            // 首先检查是否存在"幽灵包裹"（超时包裹），如果存在则执行强制同步
            var ghosts = PendingSortPackages.Values
                .Where(p => p.SortState == PackageSortState.TimedOut)
                .OrderBy(p => p.Index)
                .ToList();

            if (ghosts.Any())
            {
                Log.Warning("检测到 {Count} 个超时（幽灵）包裹，进入强制同步模式", ghosts.Count);
                
                // 记录幽灵包裹信息
                foreach (var ghost in ghosts)
                {
                    Log.Warning("幽灵包裹: {Index}|{Barcode} (格口: {Chute})", 
                        ghost.Index, ghost.Barcode, ghost.ChuteNumber);
                }

                // 执行强制同步：立即回正摆轮
                if (PendulumStates.TryGetValue(photoelectricName, out var pendulumState))
                {
                    // 将强制同步操作纳入动作队列，避免并发风险
                    if (TryGetActionChannel(photoelectricName, out var channelWriter) && channelWriter != null)
                    {
                        Log.Information("将强制同步动作加入队列 (光电: {Name})", photoelectricName);
                        
                        // 创建幽灵包裹的快照，避免闭包问题
                        var ghostsSnapshot = ghosts.ToList();
                        
                        // 将强制回正和清理逻辑包装成一个动作，放入队列
                        var success = channelWriter.TryWrite(async () => 
                        {
                            try
                            {
                                var client = GetSortingClient(photoelectricName);
                                if (client != null && client.IsConnected())
                                {
                                    await ExecuteImmediateReset(client, pendulumState, photoelectricName, "GhostPackageSync");
                                }
                                else
                                {
                                    Log.Warning("强制同步时客户端未连接，强制复位摆轮状态");
                                    pendulumState.ForceReset();
                                }
                                
                                // 在动作执行时清理幽灵包裹
                                foreach (var ghost in ghostsSnapshot)
                                {
                                    if (PendingSortPackages.TryRemove(ghost.Index, out _))
                                    {
                                        Log.Information("已清理幽灵包裹: {Index}|{Barcode}", ghost.Index, ghost.Barcode);
                                    }
                                    
                                    // 清理对应的定时器
                                    if (PackageTimers.TryRemove(ghost.Index, out var timer))
                                    {
                                        timer.Stop();
                                        timer.Dispose();
                                    }
                                }
                                
                                Log.Information("强制同步动作执行完成 (光电: {Name})", photoelectricName);
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, "执行强制同步动作时发生错误 (光电: {Name})", photoelectricName);
                                pendulumState.ForceReset();
                            }
                        });
                        
                        if (!success)
                        {
                            Log.Error("无法将强制同步动作加入队列，光电 {Name} 的队列可能已关闭", photoelectricName);
                            // 降级处理：直接强制复位状态
                            pendulumState.ForceReset();
                        }
                    }
                    else
                    {
                        Log.Warning("无法获取光电 {Name} 的动作队列，直接强制复位摆轮状态");
                        pendulumState.ForceReset();
                        
                        // 直接清理幽灵包裹（因为无法入队）
                        foreach (var ghost in ghosts)
                        {
                            if (PendingSortPackages.TryRemove(ghost.Index, out _))
                            {
                                Log.Information("已清理幽灵包裹: {Index}|{Barcode}", ghost.Index, ghost.Barcode);
                            }
                            
                            if (PackageTimers.TryRemove(ghost.Index, out var timer))
                            {
                                timer.Stop();
                                timer.Dispose();
                            }
                        }
                    }
                }

                Log.Information("强制同步已处理，本次信号不处理，等待下一个信号重新触发匹配");
                return null; // 本次不匹配任何包裹，等待下一个信号来重新触发匹配
            }

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
            
            foreach (var pkg in PendingSortPackages.Values.OrderBy(p => p.Index)) // 仍按 Index 排序保证顺序
            {
                // --- 开始应用日志上下文 ---
                var packageContext = $"[包裹{pkg.Index}|{pkg.Barcode}]";
                using (LogContext.PushProperty("PackageContext", packageContext))
                {
                    Log.Verbose("检查待处理包裹. 目标格口: {Chute}, 触发时间: {Timestamp:HH:mm:ss.fff}, 分拣状态: {SortState}", 
                        pkg.ChuteNumber, pkg.TriggerTimestamp, pkg.SortState);

                    // 基本条件检查
                    if (pkg.TriggerTimestamp == default)
                    {
                        Log.Verbose("无触发时间戳.");
                        continue;
                    }

                    // 只处理待处理状态的包裹，跳过超时和已处理的包裹
                    if (pkg.SortState != PackageSortState.Pending)
                    {
                        Log.Verbose("包裹状态为 {SortState}，跳过.", pkg.SortState);
                        continue;
                    }

                    if (!SlotBelongsToPhotoelectric(pkg.ChuteNumber, photoelectricName))
                    {
                        Log.Verbose("格口不匹配此光电.");
                        continue;
                    }

                    if (IsPackageProcessing(pkg.Barcode))
                    {
                        Log.Warning("已标记为处理中，跳过.");
                        continue;
                    } // 重要：防止重复处理

                    // 检查是否已超时 (基于 Timer 状态)
                    if (PackageTimers.TryGetValue(pkg.Index, out var timer) && !timer.Enabled)
                    {
                        Log.Warning("检测到已超时 (Timer 已禁用).");
                        continue;
                    }

                    var delay = (currentTime - pkg.TriggerTimestamp).TotalMilliseconds;
                    const double tolerance = 10.0;
                    if (delay < timeRangeLower - tolerance || delay > timeRangeUpper + tolerance)
                    {
                        Log.Debug("时间延迟不符. 延迟: {Delay:F0}ms, 范围: [{Lower}-{Upper}]ms.",
                            delay, timeRangeLower, timeRangeUpper);
                        continue;
                    }

                    // 匹配成功
                    Log.Information("匹配成功! 延迟: {Delay:F0}ms.", delay);
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

                // 1. 等待到达最佳位置
                var sortDelay = photoelectricConfig.SortingDelay;
                Log.Debug("等待分拣延迟: {SortDelay}ms", sortDelay);
                await Task.Delay(sortDelay);

                // 2. 确定目标状态和所需命令
                var targetSlot = package.ChuteNumber;
                var swingLeft = ShouldSwingLeft(targetSlot);
                var swingRight = ShouldSwingRight(targetSlot);

                var commandToSend = string.Empty;
                var commandLogName = string.Empty;
                var needsResetLater = false;

                // 3. 根据当前状态和目标状态决定是否需要发送命令
                if (swingLeft || swingRight) // 需要摆动
                {
                    // 检查当前状态是否已经符合要求
                    if ((swingLeft && pendulumState.CurrentDirection == PendulumDirection.SwingingLeft) ||
                        (swingRight && pendulumState.CurrentDirection == PendulumDirection.SwingingRight))
                    {
                        Log.Debug("摆轮已在所需状态 ({CurrentState})，无需发送摆动命令", pendulumState.GetCurrentState());
                        needsResetLater = true; // 仍然需要后续回正
                    }
                    else
                    {
                        commandToSend = swingLeft ? PendulumCommands.Module2.SwingLeft : PendulumCommands.Module2.SwingRight;
                        commandLogName = swingLeft ? "左摆" : "右摆";
                        needsResetLater = true;
                        Log.Debug("需要改变摆轮状态，确定命令: {CommandLogName} ({CommandToSend})", commandLogName, commandToSend);
                    }
                }
                else // 不需要摆动，但可能需要回正
                {
                    if (pendulumState.CurrentDirection != PendulumDirection.Reset)
                    {
                        commandToSend = pendulumState.CurrentDirection == PendulumDirection.SwingingLeft
                            ? PendulumCommands.Module2.ResetLeft
                            : PendulumCommands.Module2.ResetRight;
                        commandLogName = pendulumState.CurrentDirection == PendulumDirection.SwingingLeft 
                            ? "左回正" 
                            : "右回正";
                        Log.Debug("摆轮需要回正，确定命令: {CommandLogName} ({CommandToSend})", commandLogName, commandToSend);
                    }
                    else
                    {
                        Log.Debug("摆轮已在复位状态，无需发送命令");
                    }
                }

                // 4. 发送命令 (如果需要)
                if (!string.IsNullOrEmpty(commandToSend))
                {
                    var commandBytes = GetCommandBytes(commandToSend);
                    if (!await SendCommandWithRetryAsync(client, commandBytes, photoelectricName))
                    {
                        Log.Error("发送命令 '{CommandLogName}' ({CommandToSend}) 失败.", commandLogName, commandToSend);
                        ProcessingPackages.TryRemove(package.Barcode, out _);
                        pendulumState.ForceReset(); // 发送失败时强制复位状态
                        return;
                    }

                    // 命令发送成功，更新状态
                    if (swingLeft)
                        pendulumState.SetSwinging(true);
                    else if (swingRight)
                        pendulumState.SetSwinging(false);
                    else
                        pendulumState.SetReset();

                    Log.Information("已发送命令: {CommandLogName} ({CommandToSend}) 并更新状态为: {State}", 
                        commandLogName, commandToSend, pendulumState.GetCurrentState());
                }

                PendulumState.UpdateLastSlot(targetSlot);

                // 5. 如果需要，执行延迟回正
                if (needsResetLater)
                {
                    var resetDelay = photoelectricConfig.ResetDelay;
                    Log.Debug("进入可中断的回正延迟等待 {ResetDelay}ms...", resetDelay);

                    // 创建一个新的TCS，并注册到字典中，表示"我正在等待"
                    var tcs = new TaskCompletionSource<PackageInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
                    RegisterWaitingTask(photoelectricName, tcs);

                    try
                    {
                        // 同时等待两个事件：1. 延时到期  2. TCS被新信号唤醒
                        var completedTask = await Task.WhenAny(Task.Delay(resetDelay), tcs.Task);

                        if (completedTask == tcs.Task)
                        {
                            // --- 场景A: 被新信号提前唤醒 ---
                            var nextPackage = await tcs.Task; // 获取唤醒它的新包裹
                            Log.Information("回正延迟被新包裹 {Index}|{Barcode} 中断", nextPackage.Index, nextPackage.Barcode);

                            // 执行中断后的复杂决策逻辑
                            await HandleInterruptedReset(package, nextPackage, photoelectricName);
                        }
                        else
                        {
                            // --- 场景B: 正常延时结束，没有新信号到来 ---
                            Log.Debug("回正延迟正常结束");
                            
                            // 执行原来的回正逻辑
                            bool skipReset = ShouldSkipReset(package, photoelectricName);
                            if (!skipReset)
                            {
                                await ExecuteDelayedReset(client, pendulumState, photoelectricName);
                            }
                            else
                            {
                                Log.Information("检测到连续相同格口包裹或其他跳过条件，已跳过回正");
                            }
                        }
                    }
                    finally
                    {
                        // 无论谁先完成，都立刻尝试从字典中移除自己，防止被后续信号错误唤醒
                        UnregisterWaitingTask(photoelectricName);
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
                pendulumState?.ForceReset();
                // 异常情况下也要更新状态，避免包裹卡在Processing状态
                package.SetSortState(PackageSortState.Pending);
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
    /// 检查是否应该跳过回正操作
    /// </summary>
    /// <remarks>
    /// 这是一个非关键路径的优化方法，用于在连续相同格口包裹的情况下跳过不必要的回正操作。
    /// 注意：此方法存在微小的竞态条件风险，因为它访问 PendingSortPackages 集合时，
    /// 其他线程可能正在添加新包裹。但这不会导致系统崩溃或错分，最坏情况只是多执行一次回正。
    /// </remarks>
    private bool ShouldSkipReset(PackageInfo currentPackage, string photoelectricName)
    {
        try
        {
            // 查看 PendingSortPackages 队列中紧随其后的包裹
            var nextPackage = PendingSortPackages.Values
                .Where(p => p.Index == currentPackage.Index + 1 && SlotBelongsToPhotoelectric(p.ChuteNumber, photoelectricName))
                .FirstOrDefault();

            if (nextPackage != null && nextPackage.ChuteNumber == currentPackage.ChuteNumber)
            {
                Log.Information("检测到连续相同格口包裹 {NextIndex}|{NextBarcode} (格口: {NextChute})，将跳过回正.",
                    nextPackage.Index, nextPackage.Barcode, nextPackage.ChuteNumber);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "检查是否跳过回正时发生错误，默认执行回正");
            return false;
        }
    }

    /// <summary>
    /// 处理中断后的复杂决策逻辑
    /// </summary>
    private async Task HandleInterruptedReset(PackageInfo currentPackage, PackageInfo nextPackage, string photoelectricName)
    {
        var client = GetSortingClient(photoelectricName);
        if (client == null)
        {
            Log.Error("无法获取光电 {Name} 的客户端，中断处理失败", photoelectricName);
            return;
        }

        var pendulumState = PendulumStates[photoelectricName];

        // 1. 检查索引连续性
        if (nextPackage.Index != currentPackage.Index + 1)
        {
            Log.Warning("检测到包裹索引不连续 (当前: {Current}, 下一个: {Next})，执行立即回正",
                currentPackage.Index, nextPackage.Index);
            await ExecuteImmediateReset(client, pendulumState, photoelectricName, "IndexMismatch");
            
            // 将新包裹的动作重新入队，因为它中断了别人，但自己还没被处理
            EnqueueSortingAction(nextPackage, photoelectricName);
            return;
        }

        // 2. 检查格口关系
        if (nextPackage.ChuteNumber == currentPackage.ChuteNumber)
        {
            Log.Information("新包裹与当前包裹格口相同 (格口: {Chute})，跳过回正，直接处理新包裹",
                nextPackage.ChuteNumber);
            // 因为摆轮状态不变，直接将新包裹的动作入队即可
            EnqueueSortingAction(nextPackage, photoelectricName);
        }
        else // 不同格口
        {
            Log.Information("新包裹与当前包裹格口不同 (当前: {Current}, 新: {Next})，执行回正后处理新包裹",
                currentPackage.ChuteNumber, nextPackage.ChuteNumber);
            
            // 先回正
            await ExecuteImmediateReset(client, pendulumState, photoelectricName, "AdjacentDifferentSlot");
            
            // 延迟一个极小值，给物理回正一点时间
            await Task.Delay(20); // 这个值需要根据硬件配置调整
            
            // 将新包裹的动作入队，它会在回正后立即执行
            EnqueueSortingAction(nextPackage, photoelectricName);
        }
    }

    /// <summary>
    /// 辅助方法，用于将动作重新入队
    /// </summary>
    private void EnqueueSortingAction(PackageInfo package, string photoelectricName)
    {
        if (TryGetActionChannel(photoelectricName, out var channel) && channel != null)
        {
            var packageSnapshot = package;
            var success = channel.TryWrite(async () => await ExecuteSortingAction(packageSnapshot, photoelectricName));
            
            if (success)
            {
                Log.Debug("包裹 {Index}|{Barcode} 的分拣动作已重新入队", package.Index, package.Barcode);
            }
            else
            {
                Log.Error("无法将包裹 {Index}|{Barcode} 的分拣动作重新入队，队列可能已关闭", 
                    package.Index, package.Barcode);
            }
        }
        else
        {
            Log.Error("无法获取光电 {Name} 的动作队列，包裹 {Index}|{Barcode} 分拣动作入队失败",
                photoelectricName, package.Index, package.Barcode);
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
        public static PendulumCommands Module2 => new()
        {
            Start = "AT+STACH1=1",
            Stop = "AT+STACH1=0",
            SwingLeft = "AT+STACH2=1",
            ResetLeft = "AT+STACH2=0",
            SwingRight = "AT+STACH3=1",
            ResetRight = "AT+STACH3=0"
        };

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
        SwingingRight   // 右摆状态
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
        /// 设置摆动状态
        /// </summary>
        /// <param name="swingLeft">true表示左摆，false表示右摆</param>
        public void SetSwinging(bool swingLeft)
        {
            CurrentDirection = swingLeft ? PendulumDirection.SwingingLeft : PendulumDirection.SwingingRight;
            Log.Debug("摆轮状态更新为: {Direction}", CurrentDirection);
        }

        /// <summary>
        /// 设置复位状态
        /// </summary>
        public void SetReset()
        {
            CurrentDirection = PendulumDirection.Reset;
            Log.Debug("摆轮状态更新为: Reset");
        }

        /// <summary>
        /// 强制设置复位状态
        /// </summary>
        public void ForceReset()
        {
            CurrentDirection = PendulumDirection.Reset;
            Log.Debug("摆轮状态被强制复位");
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

        // 检查当前状态，避免重复发送回正命令
        if (pendulumState.CurrentDirection == PendulumDirection.Reset)
        {
            Log.Debug("摆轮已经处于复位状态，无需发送延迟回正命令");
            return;
        }

        // 执行回正
        var resetCommand = pendulumState.CurrentDirection == PendulumDirection.SwingingLeft
            ? PendulumCommands.Module2.ResetLeft
            : PendulumCommands.Module2.ResetRight;
        var resetCmdBytes = GetCommandBytes(resetCommand);
        var resetDir = pendulumState.CurrentDirection == PendulumDirection.SwingingLeft ? "左" : "右";

        Log.Debug("准备发送第一次延迟 {ResetDir} 回正命令 ({ResetCommand})...", resetDir, resetCommand);
        if (await SendCommandWithRetryAsync(client, resetCmdBytes, photoelectricName))
        {
            Log.Information("第一次延迟 {ResetDir} 回正命令 ({ResetCommand}) 发送成功.", resetDir, resetCommand);
            pendulumState.SetReset();

            await Task.Delay(10);

            Log.Debug("准备发送第二次延迟 {ResetDir} 回正命令 ({ResetCommand})...", resetDir, resetCommand);
            if (await SendCommandWithRetryAsync(client, resetCmdBytes, photoelectricName, maxRetries: 1))
            {
                Log.Information("第二次延迟 {ResetDir} 回正命令 ({ResetCommand}) 发送成功.", resetDir, resetCommand);
            }
            else
            {
                Log.Warning("第二次延迟 {ResetDir} 回正命令 ({ResetCommand}) 发送失败.", resetDir, resetCommand);
            }
        }
        else
        {
            Log.Error("第一次发送延迟 {ResetDir} 回正命令 ({ResetCommand}) 失败，强制复位状态.", resetDir, resetCommand);
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
            // 检查当前状态，避免重复发送回正命令
            if (pendulumState.CurrentDirection == PendulumDirection.Reset)
            {
                Log.Debug("摆轮已在复位状态，无需发送立即回正命令");
                return;
            }

            // 根据当前状态发送对应的回正命令
            var resetCommand = pendulumState.CurrentDirection == PendulumDirection.SwingingLeft
                ? PendulumCommands.Module2.ResetLeft
                : PendulumCommands.Module2.ResetRight;
            
            var commandBytes = GetCommandBytes(resetCommand);
            var resetDirection = pendulumState.CurrentDirection == PendulumDirection.SwingingLeft ? "左" : "右";

            Log.Debug("发送 {Direction} 回正命令 ({Command})...", resetDirection, resetCommand);
            
            if (await SendCommandWithRetryAsync(client, commandBytes, photoelectricName, maxRetries: 2))
            {
                pendulumState.SetReset();
                Log.Information("立即 {Direction} 回正命令发送成功 (光电: {Name})", resetDirection, photoelectricName);
                
                // 发送第二次回正命令以确保可靠性
                await Task.Delay(10);
                if (await SendCommandWithRetryAsync(client, commandBytes, photoelectricName, maxRetries: 1))
                {
                    Log.Debug("第二次 {Direction} 回正命令发送成功", resetDirection);
                }
                else
                {
                    Log.Warning("第二次 {Direction} 回正命令发送失败", resetDirection);
                }
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
}