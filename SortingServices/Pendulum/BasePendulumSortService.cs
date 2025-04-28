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

namespace SortingServices.Pendulum;

/// <summary>
///     摆轮分拣服务基类，提供单光电单摆轮和多光电多摆轮共同的功能
/// </summary>
public abstract class BasePendulumSortService : IPendulumSortService
{
    private readonly ConcurrentDictionary<string, bool> _deviceConnectionStates = new();
    private readonly ISettingsService _settingsService;
    private readonly ConcurrentQueue<double> _triggerDelays = new();
    private readonly Queue<DateTime> _triggerTimes = new();
    protected readonly ConcurrentDictionary<string, PackageInfo> MatchedPackages = new();
    protected readonly ConcurrentDictionary<int, Timer> PackageTimers = new();
    protected readonly ConcurrentDictionary<int, PackageInfo> PendingSortPackages = new();
    protected readonly ConcurrentDictionary<string, PendulumState> PendulumStates = new();
    protected readonly ConcurrentDictionary<string, ProcessingStatus> ProcessingPackages = new();
    protected readonly Timer TimeoutCheckTimer;
    private bool _disposed;
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
                    var tempTriggerTimesList = _triggerTimes.ToList();
                    var config = _settingsService.LoadSettings<PendulumSortConfig>();
                    var lowerBound = config.TriggerPhotoelectric.TimeRangeLower;
                    var upperBound = config.TriggerPhotoelectric.TimeRangeUpper;

                    Log.Debug("当前时间: {CurrentTime:HH:mm:ss.fff}, 触发队列 ({Count}) 待匹配. 允许范围: {Lower}-{Upper}ms",
                        currentTime, tempTriggerTimesList.Count, lowerBound, upperBound);

                    if (tempTriggerTimesList.Any())
                        Log.Verbose("触发队列内容: {Times}", // 使用 Verbose 记录详细队列
                            string.Join(", ", tempTriggerTimesList.Select(t =>
                                $"{t:HH:mm:ss.fff}[{(currentTime - t).TotalMilliseconds:F0}ms]")));

                    _triggerTimes.Clear(); // 清空准备重建

                    var found = false;
                    var matchCount = 0;
                    var reEnqueuedTimes = new List<DateTime>(); // 记录重新入队的时间

                    foreach (var triggerTime in tempTriggerTimesList)
                    {
                        var delay = (currentTime - triggerTime).TotalMilliseconds;

                        if (delay > upperBound) // 超过上限，丢弃
                        {
                            Log.Verbose("丢弃过时触发时间 {TriggerTime:HH:mm:ss.fff} (延迟 {Delay:F0}ms > {Upper}ms)",
                                triggerTime, delay, upperBound);
                            continue; // 跳过这个时间戳
                        }

                        if (delay < lowerBound) // 小于下限，保留供后续匹配
                        {
                            Log.Verbose("保留较新触发时间 {TriggerTime:HH:mm:ss.fff} (延迟 {Delay:F0}ms < {Lower}ms)",
                                triggerTime, delay, lowerBound);
                            _triggerTimes.Enqueue(triggerTime);
                            reEnqueuedTimes.Add(triggerTime);
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
                            // 不再将此时间戳重新入队
                        }
                        else // 如果已经找到过匹配项，则将这个也重新入队
                        {
                            _triggerTimes.Enqueue(triggerTime);
                            reEnqueuedTimes.Add(triggerTime);
                            Log.Verbose("已找到匹配项，将此时间戳 {TriggerTime:HH:mm:ss.fff} 重新入队", triggerTime);
                        }
                    } // End foreach triggerTime

                    if (matchCount > 1)
                        Log.Warning("在时间范围内找到 {MatchCount} 个潜在匹配，建议检查触发时间范围 ({Lower}-{Upper}ms)",
                            matchCount, lowerBound, upperBound);

                    if (reEnqueuedTimes.Any())
                        Log.Debug("重建后的触发队列 ({Count}): {Times}",
                            reEnqueuedTimes.Count, string.Join(", ", reEnqueuedTimes.Select(static t => t.ToString("HH:mm:ss.fff"))));
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
            Log.Information("已添加到待处理队列. 目标格口: {TargetChute}", package.ChuteNumber);

            // 创建超时定时器
            var timer = new Timer();
            timer.Elapsed += (_, _) => HandlePackageTimeout(package);

            double timeoutInterval = 10000; // 默认 10s
            string timeoutReason = "默认值";
            var photoelectricName = GetPhotoelectricNameBySlot(package.ChuteNumber);

            if (photoelectricName != null)
            {
                try {
                    var photoelectricConfig = GetPhotoelectricConfig(photoelectricName);
                    timeoutInterval = photoelectricConfig.TimeRangeUpper + 500; // 使用上限+缓冲
                    timeoutReason = $"光电 '{photoelectricName}' 上限 {photoelectricConfig.TimeRangeUpper}ms + 500ms";
                 }
                 catch(Exception ex)
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
    ///     检查分拣光电配置变更，由子类实现具体逻辑
    /// </summary>
    protected virtual void CheckSortingPhotoelectricChanges(PendulumSortConfig oldConfig, PendulumSortConfig newConfig)
    {
        // 基类不实现具体逻辑
    }

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

            if (PendingSortPackages.TryRemove(package.Index, out _))
            {
                Log.Warning("分拣超时，已从待处理队列移除.");
                // TODO: Consider setting package status to Timeout and notifying ViewModel if needed
            }
            else
            {
                // 可能已经被正常处理了，记录 Debug 信息
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

        foreach (var kvp in packagesToCheck)
        {
            var barcode = kvp.Key;
            var status = kvp.Value;
            var elapsed = now - status.StartTime;

            if (elapsed > timeoutThreshold)
            {
                Log.Warning("包裹 {Barcode} 在光电 {PhotoelectricId} 处理超时 (持续 {ElapsedSeconds:F1}s > {ThresholdSeconds}s)，将强制移除处理状态.",
                    barcode, status.PhotoelectricId, elapsed.TotalSeconds, timeoutThreshold.TotalSeconds);
                ProcessingPackages.TryRemove(barcode, out _);
                // TODO: Consider error handling or notification for persistently stuck packages
            }
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
            StartTime = DateTime.Now,
            IsProcessing = true, // IsProcessing 字段似乎冗余，但保留以防万一
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
    private void HandleTriggerPhotoelectric(string data)
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

            if (_triggerTimes.Any())
                 Log.Verbose("当前触发时间队列: {Times}", string.Join(", ", _triggerTimes.Select(static t => t.ToString("HH:mm:ss.fff"))));
        }

        // 计算并记录触发延迟 (可能意义不大，考虑移除或改为 Verbose)
        // var delay = (DateTime.Now - triggerTime).TotalMilliseconds;
        // _triggerDelays.Enqueue(delay);
        // while (_triggerDelays.Count > 1000) _triggerDelays.TryDequeue(out _);
    }

    /// <summary>
    ///     处理第二光电信号，由子类实现具体逻辑
    /// </summary>
    protected abstract void HandleSecondPhotoelectric(string data);

    /// <summary>
    ///     处理光电信号
    /// </summary>
    protected void HandlePhotoelectricSignal(string data)
    {
        var lines = data.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
             Log.Verbose("处理光电信号行: {SignalLine}", line); // 使用 Verbose 记录原始信号行
             // 简化逻辑：直接检查是否包含特定触发或分拣标识
            if (line.Contains("OCCH1:1")) // 假设 OCCH1:1 是触发信号标识
            {
                HandleTriggerPhotoelectric(line);
            }
            else if (line.Contains("OCCH2:1")) // 假设 OCCH2:1 是分拣信号标识
            {
                 HandleSecondPhotoelectric(line);
            }
            // 可以添加对 OCCH1:0 或 OCCH2:0 (下降沿) 的处理逻辑（如果需要）
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

        try {
            var config = _settingsService.LoadSettings<PendulumSortConfig>();
            var photoelectricConfigBase = GetPhotoelectricConfig(photoelectricName);
            double timeRangeLower, timeRangeUpper;
            if (photoelectricConfigBase is SortPhotoelectric sortPhotoConfig) {
                timeRangeLower = sortPhotoConfig.TimeRangeLower;
                timeRangeUpper = sortPhotoConfig.TimeRangeUpper;
            } else {
                timeRangeLower = photoelectricConfigBase.SortingTimeRangeLower;
                timeRangeUpper = photoelectricConfigBase.SortingTimeRangeUpper;
            }

            // 优化：直接迭代 ConcurrentDictionary 的 Values，避免 ToList() 的开销
            // 注意：迭代过程中字典可能变化，但对于查找第一个匹配项通常没问题
             foreach (var pkg in PendingSortPackages.Values.OrderBy(p => p.Index)) // 仍按 Index 排序保证顺序
             {
                // --- 开始应用日志上下文 ---
                var packageContext = $"[包裹{pkg.Index}|{pkg.Barcode}]";
                using (LogContext.PushProperty("PackageContext", packageContext))
                {
                    Log.Verbose("检查待处理包裹. 目标格口: {Chute}, 触发时间: {Timestamp:HH:mm:ss.fff}", pkg.ChuteNumber, pkg.TriggerTimestamp);

                    // 基本条件检查
                    if (pkg.TriggerTimestamp == default) { Log.Verbose("无触发时间戳."); continue; }
                    if (!SlotBelongsToPhotoelectric(pkg.ChuteNumber, photoelectricName)) { Log.Verbose("格口不匹配此光电."); continue; }
                    if (IsPackageProcessing(pkg.Barcode)) { Log.Warning("已标记为处理中，跳过."); continue; } // 重要：防止重复处理

                    // 检查是否已超时 (基于 Timer 状态)
                    if (PackageTimers.TryGetValue(pkg.Index, out var timer) && !timer.Enabled)
                    {
                        Log.Warning("检测到已超时 (Timer 已禁用).");
                        // 超时处理逻辑（移除）应该由 HandlePackageTimeout 完成，这里只跳过
                        continue;
                    }

                    // 时间延迟检查
                    var delay = (currentTime - pkg.TriggerTimestamp).TotalMilliseconds;
                    // 增加一个小的容错范围（例如 10ms）来处理边界情况
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
                     matchedPackage = pkg;
                     break; // 找到第一个匹配的就跳出循环
                 } // --- 日志上下文结束 ---
             }
         } catch(Exception ex)
         {
            Log.Error(ex, "匹配包裹时发生异常 (光电: {PhotoelectricName}).", photoelectricName);
            return null; // 发生异常时返回 null
         }


        if (matchedPackage != null)
        {
            // 停止并移除对应的超时定时器
            if (PackageTimers.TryRemove(matchedPackage.Index, out var timer))
            {
                timer.Stop();
                timer.Dispose();
                Log.Debug("[包裹{Index}|{Barcode}] 匹配成功，已停止并移除超时定时器.", matchedPackage.Index, matchedPackage.Barcode);
            }
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
        var photoelectricConfig = sortConfig.SortingPhotoelectrics?.FirstOrDefault(p => p.Name == photoelectricName);

        if (photoelectricConfig != null)
        {
             return photoelectricConfig;
        }

        // 如果在分拣光电中找不到，检查是否为触发光电 (适用于单摆轮)
        if (photoelectricName == "触发光电" || photoelectricName == "默认")
        {
             if (sortConfig.TriggerPhotoelectric != null) {
                 return sortConfig.TriggerPhotoelectric;
             } else {
                 throw new KeyNotFoundException("未配置触发光电，无法获取 '{photoelectricName}' 的配置.");
             }
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
            TcpClientService? client = null;
            PendulumState? pendulumState = null;
            TriggerPhotoelectric? photoelectricConfig = null;

            try
            {
                client = GetSortingClient(photoelectricName);
                if (client == null || !client.IsConnected())
                {
                    Log.Warning("分拣客户端 '{Name}' 未连接或未找到，无法执行分拣.", photoelectricName);
                    // 即使无法执行，也需要从 ProcessingPackages 移除
                    ProcessingPackages.TryRemove(package.Barcode, out _);
                    // 考虑是否需要将包裹状态设为错误并分配到错误口
                    return;
                }

                if (!PendulumStates.TryGetValue(photoelectricName, out pendulumState))
                {
                     Log.Error("无法找到光电 '{Name}' 的摆轮状态.", photoelectricName);
                     ProcessingPackages.TryRemove(package.Barcode, out _);
                     return;
                }
                 photoelectricConfig = GetPhotoelectricConfig(photoelectricName);


                // 1. 等待到达最佳位置
                var sortDelay = photoelectricConfig.SortingDelay;
                Log.Debug("等待分拣延迟: {SortDelay}ms", sortDelay);
                await Task.Delay(sortDelay);

                // 2. 确定并发送摆动/回正命令
                var targetSlot = package.ChuteNumber;
                string commandToSend = string.Empty;
                string commandLogName = string.Empty;
                bool needsResetLater = false;

                bool swingLeft = ShouldSwingLeft(targetSlot);
                bool swingRight = ShouldSwingRight(targetSlot);

                 // 核心逻辑：决定是否摆动以及摆动方向
                 if (swingLeft || swingRight) // 需要摆动
                 {
                     commandToSend = swingLeft ? PendulumCommands.Module2.SwingLeft : PendulumCommands.Module2.SwingRight;
                     commandLogName = swingLeft ? "左摆" : "右摆";
                     pendulumState.SetSwing(); // 标记为摆动状态
                     needsResetLater = true; // 摆动后需要回正
                     Log.Debug("确定命令: {CommandLogName} ({CommandToSend})", commandLogName, commandToSend);
                 }
                 else // 不需要摆动，但可能需要回正
                 {
                      if (pendulumState.GetCurrentState() == "Swing") // 如果当前是摆动状态
                      {
                           commandToSend = pendulumState.LastSlot % 2 == 1 ? PendulumCommands.Module2.ResetLeft : PendulumCommands.Module2.ResetRight;
                           commandLogName = pendulumState.LastSlot % 2 == 1 ? "左回正(之前是奇数口)" : "右回正(之前是偶数口)";
                           pendulumState.SetReset(); // 标记为回正状态
                           Log.Debug("当前无需摆动但摆轮非复位状态，确定命令: {CommandLogName} ({CommandToSend})", commandLogName, commandToSend);
                      } else {
                           Log.Debug("当前无需摆动且摆轮已复位，无需发送命令.");
                      }
                 }


                // 3. 发送命令 (如果需要)
                if (!string.IsNullOrEmpty(commandToSend))
                {
                    var commandBytes = GetCommandBytes(commandToSend);
                    if (!await SendCommandWithRetryAsync(client, commandBytes, photoelectricName))
                    {
                        Log.Error("发送命令 '{CommandLogName}' ({CommandToSend}) 失败.", commandLogName, commandToSend);
                         ProcessingPackages.TryRemove(package.Barcode, out _); // 发送失败，移除处理标记
                        return;
                    }
                    Log.Information("已发送命令: {CommandLogName} ({CommandToSend})", commandLogName, commandToSend);
                }

                pendulumState.UpdateLastSlot(targetSlot); // 记录这次处理的格口

                // 4. 如果需要，设置回正定时器
                if (needsResetLater)
                {
                     var resetDelay = photoelectricConfig.ResetDelay;
                     Log.Debug("分拣动作完成，将在 {ResetDelay}ms 后执行回正检查.", resetDelay);

                    var resetTimer = new Timer { Interval = resetDelay, AutoReset = false };
                    resetTimer.Elapsed += async (_, _) =>
                    {
                        resetTimer.Stop(); // 确保只执行一次
                         // 在 Timer 回调中再次应用上下文
                         using (LogContext.PushProperty("PackageContext", packageContext))
                         {
                             Log.Debug("回正定时器触发.");
                             try
                             {
                                 // 检查客户端连接状态
                                 if (client == null || !client.IsConnected()) {
                                     Log.Warning("回正时客户端 '{Name}' 未连接.", photoelectricName);
                                     pendulumState.ForceReset(); // 强制标记为复位
                                     return;
                                 }

                                 // 检查下一个包裹是否需要跳过回正 (逻辑简化)
                                 var nextPackageIndex = package.Index + 1;
                                 bool skipReset = PendingSortPackages.TryGetValue(nextPackageIndex, out var nextPackage) &&
                                                  nextPackage.ChuteNumber == targetSlot && // 下一个目标格口相同
                                                  SlotBelongsToPhotoelectric(nextPackage.ChuteNumber, photoelectricName); // 且属于同一个光电

                                 if (skipReset)
                                 {
                                     Log.Information("检测到下一个包裹 {NextIndex}|{NextBarcode} 目标格口 ({NextChute}) 与当前相同，跳过回正.",
                                                     nextPackage!.Index, nextPackage.Barcode, nextPackage.ChuteNumber);
                                 }
                                 else
                                 {
                                     // 执行回正
                                     var resetCommand = swingLeft ? PendulumCommands.Module2.ResetLeft : PendulumCommands.Module2.ResetRight;
                                     var resetCmdBytes = GetCommandBytes(resetCommand);
                                     var resetDir = swingLeft ? "左" : "右";

                                     if (await SendCommandWithRetryAsync(client, resetCmdBytes, photoelectricName))
                                     {
                                          pendulumState.SetReset();
                                          Log.Information("已发送 {ResetDir} 回正命令 ({ResetCommand}). 原因: {Reason}",
                                                          resetDir, resetCommand, skipReset ? "错误逻辑?" : (nextPackage != null ? "下一个格口不同" : "无后续包裹"));
                                     }
                                     else
                                     {
                                          Log.Error("发送 {ResetDir} 回正命令 ({ResetCommand}) 失败，强制复位状态.", resetDir, resetCommand);
                                          pendulumState.ForceReset();
                                     }
                                 }
                             }
                             catch (Exception ex)
                             {
                                 Log.Error(ex, "执行回正逻辑时发生错误 (光电: {Name}).", photoelectricName);
                                 pendulumState.ForceReset();
                             }
                             finally
                             {
                                 resetTimer.Dispose(); // 释放定时器资源
                             }
                         } // Timer LogContext 结束
                    };
                    resetTimer.Start();
                } else {
                    // 如果不需要回正，说明动作已完成，可以从 Pending 队列移除
                     // 修正：无论是否需要回正，分拣动作主要部分完成时就应该移除
                     if (PendingSortPackages.TryRemove(package.Index, out _)) {
                         Log.Debug("分拣动作完成 (无需回正)，已从待处理队列移除.");
                     } else {
                          Log.Warning("尝试移除已完成(无需回正)的包裹失败 (可能已被移除).");
                     }
                }


                // 修正：包裹移除逻辑应该在回正定时器设置之后，或者在回正定时器内部完成时执行。
                // 为了确保包裹在物理动作完成前不被错误移除，我们将移除操作放到回正定时器回调中（如果需要回正）
                // 或者在确定无需回正时立即移除。
                if (!needsResetLater) {
                    if (PendingSortPackages.TryRemove(package.Index, out _)) {
                         Log.Debug("分拣动作完成 (无需回正)，已从待处理队列移除.");
                     } else {
                          Log.Warning("尝试移除已完成(无需回正)的包裹失败 (可能已被移除).");
                     }
                } else {
                    // 移除操作将在回正定时器回调中进行 (修改回正定时器逻辑)
                    // 在回正定时器回调的 finally 块中添加移除逻辑
                    // (需要传递 package.Index 到回调中)
                    // **修改回正定时器回调逻辑**
                     // 移除上述 resetTimer.Elapsed 中的移除逻辑，改为在 finally 中执行
                     // 需要修改 Elapsed 事件处理器的签名以接收 package.Index 或修改类的状态
                     // 为了简化，我们在执行完主要动作后就认为可以移除了，后续的回正是独立动作
                     if (PendingSortPackages.TryRemove(package.Index, out _)) {
                         Log.Debug("分拣动作完成 (等待回正)，已从待处理队列移除.");
                     } else {
                          Log.Warning("尝试移除已完成(等待回正)的包裹失败 (可能已被移除).");
                     }

                }


            }
            catch (Exception ex)
            {
                Log.Error(ex, "执行分拣动作时发生异常.");
                // 异常时尝试移除
                PendingSortPackages.TryRemove(package.Index, out _);
                // 强制复位摆轮状态？
                pendulumState?.ForceReset();
            }
            finally
            {
                // 无论成功失败，最终都要从 ProcessingPackages 中移除标记
                if (ProcessingPackages.TryRemove(package.Barcode, out _))
                {
                    Log.Debug("已从处理中状态移除.");
                }
                else
                {
                    // 这通常不应该发生，但也记录一下
                    Log.Warning("尝试从处理中状态移除失败 (可能已被移除).");
                }
            }
        } // --- 日志上下文结束 ---
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
        public bool IsProcessing { get; init; }
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
    ///     摆轮状态类
    /// </summary>
    protected class PendulumState
    {
        private bool _isInReset = true;
        public int LastSlot { get; private set; }

        public void SetSwing()
        {
            _isInReset = false;
        }

        public void SetReset()
        {
            _isInReset = true;
        }

        public void UpdateLastSlot(int slot)
        {
            LastSlot = slot;
        }

        public string GetCurrentState()
        {
            return _isInReset ? "Reset" : "Swing";
        }

        public void ForceReset()
        {
            _isInReset = true;
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
                    Log.Warning("客户端 {Name} 未连接 (尝试次数 {Attempt}/{MaxRetries}). 尝试重连...", photoelectricName, i + 1, maxRetries);
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
                Log.Warning(ex, "发送命令 {Command} 到 {Name} 失败 (尝试次数 {Attempt}/{MaxRetries}).", commandString, photoelectricName, i + 1, maxRetries);
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
}