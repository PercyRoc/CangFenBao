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

                    if (tempTriggerTimesList.Count != 0)
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

                    if (reEnqueuedTimes.Count != 0)
                        Log.Debug("重建后的触发队列 ({Count}): {Times}",
                            reEnqueuedTimes.Count,
                            string.Join(", ", reEnqueuedTimes.Select(static t => t.ToString("HH:mm:ss.fff"))));
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
    ///     分拣信号事件
    /// </summary>
    protected event EventHandler<string>? SortingSignalReceived;

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
            if (line.Contains("OCCH1:1"))
            {
                HandleTriggerPhotoelectric(line);
            }
            else if (line.Contains("OCCH2:1"))
            {
                // 触发分拣信号事件
                SortingSignalReceived?.Invoke(this, line);
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
                    Log.Verbose("检查待处理包裹. 目标格口: {Chute}, 触发时间: {Timestamp:HH:mm:ss.fff}", pkg.ChuteNumber,
                        pkg.TriggerTimestamp);

                    // 基本条件检查
                    if (pkg.TriggerTimestamp == default)
                    {
                        Log.Verbose("无触发时间戳.");
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

                // 5. 如果需要，设置回正定时器
                if (needsResetLater)
                {
                    var resetDelay = photoelectricConfig.ResetDelay;
                    Log.Debug("分拣动作完成，将在 {ResetDelay}ms 后执行回正检查.", resetDelay);

                    var resetTimer = new Timer { Interval = resetDelay, AutoReset = false };
                    var resetTimerCancellationSource = new CancellationTokenSource();
                    var eventHandlerRemoved = false;
                    var lockObject = new object();
                    
                    // 注册分拣信号处理
                    void OnSortingSignal(object? sender, string signal)
                    {
                        lock (lockObject)
                        {
                            // 如果定时器已经被取消或事件处理器已移除，直接返回
                            if (resetTimerCancellationSource.Token.IsCancellationRequested || eventHandlerRemoved)
                                return;

                            try
                            {
                                Log.Debug("延迟回正期间收到新分拣信号: {Signal}", signal);
                                
                                // 找到当前匹配的包裹（通过分拣信号匹配）
                                var matchedPackage = MatchPackageForSorting(photoelectricName);
                                if (matchedPackage == null)
                                {
                                    Log.Debug("延迟回正期间收到新分拣信号，但未匹配到包裹");
                                    return;
                                }

                                Log.Information("延迟回正期间匹配到新包裹 {NewIndex}|{NewBarcode} (格口: {NewChute})", 
                                    matchedPackage.Index, matchedPackage.Barcode, matchedPackage.ChuteNumber);

                                // 检查包裹索引连续性
                                var isIndexContinuous = matchedPackage.Index == package.Index + 1;
                                if (!isIndexContinuous)
                                {
                                    Log.Warning("检测到包裹索引不连续 (当前: {CurrentIndex}, 新包裹: {NewIndex})，需要回正", 
                                        package.Index, matchedPackage.Index);
                                    
                                    resetTimer.Stop();
                                    resetTimerCancellationSource.Cancel();
                                    
                                    // 执行立即回正
                                    _ = Task.Run(async () =>
                                    {
                                        try
                                        {
                                            await ExecuteImmediateReset(client, pendulumState, photoelectricName, packageContext);
                                        }
                                        catch (Exception ex)
                                        {
                                            Log.Error(ex, "执行索引不连续回正时发生错误");
                                        }
                                    });
                                    return;
                                }

                                // 检查格口关系
                                if (matchedPackage.ChuteNumber == targetSlot)
                                {
                                    // 相同格口，终止当前计时器，开始新包裹的计时器
                                    Log.Information("新包裹与当前包裹格口相同 ({Chute})，终止当前计时器，开始新包裹的延迟回正计时器", targetSlot);
                                    
                                    resetTimer.Stop();
                                    resetTimerCancellationSource.Cancel();
                                    
                                    // 开始新包裹的延迟回正
                                    _ = Task.Run(async () =>
                                    {
                                        try
                                        {
                                            await StartNewPackageDelayedReset(client, pendulumState, photoelectricName, 
                                                matchedPackage, packageContext);
                                        }
                                        catch (Exception ex)
                                        {
                                            Log.Error(ex, "开始新包裹延迟回正时发生错误");
                                        }
                                    });
                                    return;
                                }

                                // 不同格口，检查是否属于同一个摆轮
                                var belongsToSamePendulum = SlotBelongsToPhotoelectric(matchedPackage.ChuteNumber, photoelectricName);
                                
                                if (belongsToSamePendulum)
                                {
                                    // 属于同一个摆轮的不同格口，回正后延迟20ms发送摆动命令
                                    Log.Information("新包裹属于同一摆轮的不同格口 (当前: {CurrentChute}, 新: {NewChute})，回正后延迟摆动", 
                                        targetSlot, matchedPackage.ChuteNumber);
                                    
                                    resetTimer.Stop();
                                    resetTimerCancellationSource.Cancel();
                                    
                                    // 执行回正并延迟摆动
                                    _ = Task.Run(async () =>
                                    {
                                        try
                                        {
                                            await ExecuteResetAndDelayedSwing(client, pendulumState, photoelectricName, 
                                                matchedPackage, packageContext);
                                        }
                                        catch (Exception ex)
                                        {
                                            Log.Error(ex, "执行回正并延迟摆动时发生错误");
                                        }
                                    });
                                }
                                else
                                {
                                    // 不属于同一个摆轮，立即回正
                                    Log.Information("新包裹不属于同一摆轮 (当前摆轮: {CurrentPendulum}, 新格口: {NewChute})，立即回正", 
                                        photoelectricName, matchedPackage.ChuteNumber);
                                    
                                    resetTimer.Stop();
                                    resetTimerCancellationSource.Cancel();
                                    
                                    // 执行立即回正
                                    _ = Task.Run(async () =>
                                    {
                                        try
                                        {
                                            await ExecuteImmediateReset(client, pendulumState, photoelectricName, packageContext);
                                        }
                                        catch (Exception ex)
                                        {
                                            Log.Error(ex, "执行不同摆轮立即回正时发生错误");
                                        }
                                    });
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, "处理延迟回正期间的新分拣信号时发生错误");
                            }
                        }
                    }

                    // 订阅分拣信号事件
                    SortingSignalReceived += OnSortingSignal;
                    
                    resetTimer.Elapsed += async (_, _) =>
                    {
                        lock (lockObject)
                        {
                            if (eventHandlerRemoved) return;
                            eventHandlerRemoved = true;
                            SortingSignalReceived -= OnSortingSignal;
                        }
                        
                        resetTimer.Stop();
                        
                        using (LogContext.PushProperty("PackageContext", packageContext))
                        {
                            Log.Debug("回正定时器触发.");
                            try
                            {
                                if (client == null || !client.IsConnected())
                                {
                                    Log.Warning("回正时客户端 '{Name}' 未连接.", photoelectricName);
                                    pendulumState.ForceReset();
                                    return;
                                }

                                // 检查是否有相同格口且索引连续的待处理包裹，如果有则跳过回正
                                var skipReset = false;
                                PackageInfo? sameSlotPackage = null;
                                
                                foreach (var pendingPackage in PendingSortPackages.Values.OrderBy(p => p.Index))
                                {
                                    // 跳过当前包裹
                                    if (pendingPackage.Index <= package.Index) continue;
                                    
                                    // 检查是否属于同一个光电
                                    if (SlotBelongsToPhotoelectric(pendingPackage.ChuteNumber, photoelectricName))
                                    {
                                        // 检查索引连续性
                                        var isIndexContinuous = pendingPackage.Index == package.Index + 1;
                                        
                                        if (pendingPackage.ChuteNumber == targetSlot && isIndexContinuous)
                                        {
                                            skipReset = true;
                                            sameSlotPackage = pendingPackage;
                                            break;
                                        }
                                        else
                                        {
                                            // 发现不同格口的包裹或索引不连续，需要回正
                                            if (!isIndexContinuous)
                                            {
                                                Log.Debug("发现索引不连续的包裹 {PkgIndex} (当前: {CurrentIndex})，需要回正", 
                                                    pendingPackage.Index, package.Index);
                                            }
                                            break;
                                        }
                                    }
                                }

                                if (skipReset && sameSlotPackage != null)
                                {
                                    Log.Information("检测到相同格口且索引连续的待处理包裹 {NextIndex}|{NextBarcode} (格口: {NextChute})，跳过回正.",
                                        sameSlotPackage.Index, sameSlotPackage.Barcode, sameSlotPackage.ChuteNumber);
                                    return;
                                }

                                // 执行回正
                                await ExecuteDelayedReset(client, pendulumState, photoelectricName);
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, "执行回正逻辑时发生错误 (光电: {Name}).", photoelectricName);
                                pendulumState.ForceReset();
                            }
                            finally
                            {
                                resetTimer.Dispose();
                                resetTimerCancellationSource.Dispose();
                            }
                        }
                    };
                    resetTimer.Start();
                }

                // 从待处理队列中移除包裹
                if (PendingSortPackages.TryRemove(package.Index, out _))
                {
                    Log.Debug("分拣动作完成，已从待处理队列移除. {NeedsReset}", 
                        needsResetLater ? "等待回正" : "无需回正");
                }
                else
                {
                    Log.Warning("尝试移除已完成的包裹失败 (可能已被移除).");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "执行分拣动作时发生异常.");
                PendingSortPackages.TryRemove(package.Index, out _);
                pendulumState?.ForceReset();
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
    /// 开始新包裹的延迟回正计时器
    /// </summary>
    private async Task StartNewPackageDelayedReset(TcpClientService? client, PendulumState pendulumState, 
        string photoelectricName, PackageInfo newPackage, string packageContext)
    {
        using (LogContext.PushProperty("PackageContext", $"[包裹{newPackage.Index}|{newPackage.Barcode}]"))
        {
            Log.Debug("开始新包裹的延迟回正计时器 (格口: {Chute}).", newPackage.ChuteNumber);
            
            if (client == null || !client.IsConnected())
            {
                Log.Warning("开始新包裹延迟回正时客户端 '{Name}' 未连接.", photoelectricName);
                pendulumState.ForceReset();
                return;
            }

            try
            {
                // 获取新包裹的光电配置和回正延迟时间
                var photoelectricConfig = GetPhotoelectricConfig(photoelectricName);
                var resetDelay = photoelectricConfig.ResetDelay;
                
                Log.Debug("新包裹延迟回正时间: {ResetDelay}ms", resetDelay);

                // 等待延迟时间
                await Task.Delay(resetDelay);

                // 检查是否还有更新的相同格口包裹
                var hasNewerSameSlotPackage = false;
                foreach (var pendingPackage in PendingSortPackages.Values.OrderBy(p => p.Index))
                {
                    // 跳过当前及之前的包裹
                    if (pendingPackage.Index <= newPackage.Index) continue;
                    
                    // 检查是否属于同一个光电且相同格口
                    if (SlotBelongsToPhotoelectric(pendingPackage.ChuteNumber, photoelectricName) &&
                        pendingPackage.ChuteNumber == newPackage.ChuteNumber)
                    {
                        // 检查索引连续性
                        var isIndexContinuous = pendingPackage.Index == newPackage.Index + 1;
                        if (isIndexContinuous)
                        {
                            hasNewerSameSlotPackage = true;
                            Log.Information("检测到更新的相同格口且索引连续的包裹 {NewerIndex}|{NewerBarcode} (格口: {Chute})，跳过回正.",
                                pendingPackage.Index, pendingPackage.Barcode, pendingPackage.ChuteNumber);
                            break;
                        }
                    }
                }

                if (!hasNewerSameSlotPackage)
                {
                    // 执行延迟回正
                    await ExecuteDelayedReset(client, pendulumState, photoelectricName);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "新包裹延迟回正过程中发生错误 (光电: {Name}).", photoelectricName);
                pendulumState.ForceReset();
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
    /// 执行立即回正
    /// </summary>
    private async Task ExecuteImmediateReset(TcpClientService? client, PendulumState pendulumState, 
        string photoelectricName, string packageContext)
    {
        using (LogContext.PushProperty("PackageContext", packageContext))
        {
            Log.Debug("执行立即回正.");
            
            if (client == null || !client.IsConnected())
            {
                Log.Warning("立即回正时客户端 '{Name}' 未连接.", photoelectricName);
                pendulumState.ForceReset();
                return;
            }

            // 检查当前状态，避免重复发送回正命令
            if (pendulumState.CurrentDirection == PendulumDirection.Reset)
            {
                Log.Debug("摆轮已经处于复位状态，无需发送立即回正命令");
                return;
            }

            // 执行回正
            var resetCommand = pendulumState.CurrentDirection == PendulumDirection.SwingingLeft
                ? PendulumCommands.Module2.ResetLeft
                : PendulumCommands.Module2.ResetRight;
            var resetCmdBytes = GetCommandBytes(resetCommand);
            var resetDir = pendulumState.CurrentDirection == PendulumDirection.SwingingLeft ? "左" : "右";

            Log.Debug("准备发送立即 {ResetDir} 回正命令 ({ResetCommand})...", resetDir, resetCommand);
            if (await SendCommandWithRetryAsync(client, resetCmdBytes, photoelectricName))
            {
                Log.Information("立即 {ResetDir} 回正命令 ({ResetCommand}) 发送成功.", resetDir, resetCommand);
                pendulumState.SetReset();
            }
            else
            {
                Log.Error("发送立即 {ResetDir} 回正命令 ({ResetCommand}) 失败，强制复位状态.", resetDir, resetCommand);
                pendulumState.ForceReset();
            }
        }
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
    /// 执行回正并延迟摆动
    /// </summary>
    private async Task ExecuteResetAndDelayedSwing(TcpClientService? client, PendulumState pendulumState, 
        string photoelectricName, PackageInfo newPackage, string packageContext)
    {
        using (LogContext.PushProperty("PackageContext", packageContext))
        {
            Log.Debug("执行回正并延迟摆动 (新包裹: {NewIndex}|{NewBarcode}, 格口: {NewChute}).", 
                newPackage.Index, newPackage.Barcode, newPackage.ChuteNumber);
            
            if (client == null || !client.IsConnected())
            {
                Log.Warning("回正并延迟摆动时客户端 '{Name}' 未连接.", photoelectricName);
                pendulumState.ForceReset();
                return;
            }

            // 1. 先执行回正
            if (pendulumState.CurrentDirection != PendulumDirection.Reset)
            {
                var resetCommand = pendulumState.CurrentDirection == PendulumDirection.SwingingLeft
                    ? PendulumCommands.Module2.ResetLeft
                    : PendulumCommands.Module2.ResetRight;
                var resetCmdBytes = GetCommandBytes(resetCommand);
                var resetDir = pendulumState.CurrentDirection == PendulumDirection.SwingingLeft ? "左" : "右";

                Log.Debug("准备发送 {ResetDir} 回正命令 ({ResetCommand})...", resetDir, resetCommand);
                if (await SendCommandWithRetryAsync(client, resetCmdBytes, photoelectricName))
                {
                    Log.Information("{ResetDir} 回正命令 ({ResetCommand}) 发送成功.", resetDir, resetCommand);
                    pendulumState.SetReset();
                }
                else
                {
                    Log.Error("发送 {ResetDir} 回正命令 ({ResetCommand}) 失败，强制复位状态.", resetDir, resetCommand);
                    pendulumState.ForceReset();
                    return;
                }
            }
            else
            {
                Log.Debug("摆轮已经处于复位状态，跳过回正步骤");
            }

            // 2. 延迟20ms
            Log.Debug("回正完成，延迟20ms后发送摆动命令...");
            await Task.Delay(20);

            // 3. 发送新包裹的摆动命令
            var targetSlot = newPackage.ChuteNumber;
            var swingLeft = ShouldSwingLeft(targetSlot);
            var swingRight = ShouldSwingRight(targetSlot);

            if (swingLeft || swingRight)
            {
                var swingCommand = swingLeft ? PendulumCommands.Module2.SwingLeft : PendulumCommands.Module2.SwingRight;
                var swingCmdBytes = GetCommandBytes(swingCommand);
                var swingDir = swingLeft ? "左摆" : "右摆";

                Log.Debug("准备发送延迟摆动命令: {SwingDir} ({SwingCommand})...", swingDir, swingCommand);
                if (await SendCommandWithRetryAsync(client, swingCmdBytes, photoelectricName))
                {
                    Log.Information("延迟摆动命令 {SwingDir} ({SwingCommand}) 发送成功.", swingDir, swingCommand);
                    pendulumState.SetSwinging(swingLeft);
                    PendulumState.UpdateLastSlot(targetSlot);
                }
                else
                {
                    Log.Error("发送延迟摆动命令 {SwingDir} ({SwingCommand}) 失败.", swingDir, swingCommand);
                    pendulumState.ForceReset();
                }
            }
            else
            {
                Log.Debug("新包裹格口 {Chute} 无需摆动", targetSlot);
            }
        }
    }
}