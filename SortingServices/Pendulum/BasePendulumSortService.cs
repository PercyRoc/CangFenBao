using System.Collections.Concurrent;
using System.Text;
using System.Timers;
using Common.Events;
using Common.Models.Package;
using Common.Models.Settings.Sort.PendulumSort;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.TCP;
using JetBrains.Annotations;
using Prism.Events;
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
    private readonly IEventAggregator _eventAggregator;

    // 【新增】用于管理每个摆轮的等待超时定时器
    private readonly ConcurrentDictionary<string, Timer> _pendulumWaitingTimers = new();

    // 【新增】光电信号状态跟踪，用于验证信号完整性
    private readonly ConcurrentDictionary<string, PhotoelectricSignalState> _signalStates = new();

    // 【生产者-消费者模式】用于解耦信号接收和处理的事件队列
    private readonly BlockingCollection<(string PhotoelectricName, DateTime SignalTime)> _sortingSignalQueue =
        new(new ConcurrentQueue<(string, DateTime)>());

    private readonly Queue<DateTime> _triggerTimes = new();
    protected readonly ConcurrentDictionary<string, DateTime> LastSignalTimes = new(); // 用于存储上次收到信号的时间

    protected readonly ConcurrentDictionary<int, Timer> PackageTimers = new();
    protected readonly ConcurrentDictionary<int, PackageInfo> PendingSortPackages = new();
    protected readonly ConcurrentDictionary<string, PendulumState> PendulumStates = new();
    protected readonly ConcurrentDictionary<string, ProcessingStatus> ProcessingPackages = new();


    protected readonly ISettingsService SettingsService;
    protected readonly Timer TimeoutCheckTimer;
    private bool _disposed;
    private Task? _signalConsumerTask;
    protected CancellationTokenSource? CancellationTokenSource;
    protected bool IsRunningFlag;
    protected TcpClientService? TriggerClient;

    protected BasePendulumSortService(ISettingsService settingsService, IEventAggregator eventAggregator)
    {
        SettingsService = settingsService;
        _eventAggregator = eventAggregator;

        // 初始化超时检查定时器
        TimeoutCheckTimer = new Timer(2000); // 2秒检查一次
        TimeoutCheckTimer.Elapsed += CheckTimeoutPackages;
        TimeoutCheckTimer.AutoReset = true;

        // 确保硬件命令调度器已启动
        HardwareCommandScheduler.Start();
    }

    public event EventHandler<(string Name, bool Connected)>? DeviceConnectionStatusChanged;

    /// <summary>
    ///     分拣完成事件
    /// </summary>
    public event EventHandler<PackageInfo>? SortingCompleted;

    public abstract Task InitializeAsync(PendulumSortConfig configuration);

    public abstract Task StartAsync();

    public abstract Task StopAsync();

    public bool IsRunning()
    {
        return IsRunningFlag;
    }

    public void ProcessPackage(PackageInfo package)
    {
        var processingTime = DateTime.Now;

        // 通过 EventAggregator 发布包裹处理事件
        try
        {
            _eventAggregator.GetEvent<PackageProcessingEvent>().Publish(processingTime);
            Log.Debug("已通过 EventAggregator 发布 PackageProcessingEvent.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "通过 EventAggregator 发布 PackageProcessingEvent 时发生错误");
        }

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
                Log.Debug("已有触发时间戳 {Timestamp:HH:mm:ss.fff}，直接计算处理时间.", package.TriggerTimestamp);

                // 【修改】直接使用触发时间戳计算处理时间：当前时间 - 触发时间
                var currentTime = DateTime.Now;
                var calculatedProcessingTime = (currentTime - package.TriggerTimestamp).TotalMilliseconds;
                package.ProcessingTime = calculatedProcessingTime;
                Log.Information(
                    "包裹 {Barcode} 处理时间已计算: {ProcessingTime:F0}ms (触发: {TriggerTime:HH:mm:ss.fff} → 当前: {CurrentTime:HH:mm:ss.fff})",
                    package.Barcode, calculatedProcessingTime, package.TriggerTimestamp, currentTime);
            }
            else
            {
                Log.Debug("开始查找匹配的触发时间.");
                DateTime? matchedTriggerTime = null;

                lock (_triggerTimes) // 确保线程安全
                {
                    var currentTime = DateTime.Now;
                    var config = SettingsService.LoadSettings<PendulumSortConfig>();
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
                            // 【修改】不再在这里设置处理时间，而是在匹配成功后统一计算
                        }
                        else // 如果已经找到过匹配项，则将这个也保留
                        {
                            stillValidTimes.Enqueue(triggerTime);
                            Log.Verbose("已找到匹配项，将此时间戳 {TriggerTime:HH:mm:ss.fff} 保留", triggerTime);
                        }
                    } // End while TryDequeue

                    // 将剩余的有效时间放回主队列
                    while (stillValidTimes.TryDequeue(out var validTime)) _triggerTimes.Enqueue(validTime);

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

                    // 【修改】匹配成功后，计算处理时间：当前时间 - 触发时间
                    var currentTime = DateTime.Now;
                    var calculatedProcessingTime = (currentTime - matchedTriggerTime.Value).TotalMilliseconds;
                    package.ProcessingTime = calculatedProcessingTime;
                    Log.Information(
                        "包裹 {Barcode} 处理时间已计算: {ProcessingTime:F0}ms (触发: {TriggerTime:HH:mm:ss.fff} → 当前: {CurrentTime:HH:mm:ss.fff})",
                        package.Barcode, calculatedProcessingTime, matchedTriggerTime.Value, currentTime);
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

            // 【新增】验证包裹状态是否正确设置
            if (package.SortState != PackageSortState.Pending)
            {
                Log.Warning("⚠️ 包裹 {Index}|{Barcode} 状态设置异常: 期望状态=Pending, 实际状态={ActualState}",
                    package.Index, package.Barcode, package.SortState);
                // 强制设置为待处理状态
                package.SetSortState(PackageSortState.Pending);
            }
            else
            {
                Log.Debug("✅ 包裹 {Index}|{Barcode} 状态设置正确: {SortState}",
                    package.Index, package.Barcode, package.SortState);
            }

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

                var config = SettingsService.LoadSettings<PendulumSortConfig>();
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
    ///     调度延迟回正命令到硬件命令调度器
    /// </summary>
    private void ScheduleDelayedResetCommand(TcpClientService? client, PendulumState pendulumState,
        string photoelectricName, int resetDelay, int slotToResetFrom)
    {
        if (client == null)
        {
            Log.Warning("无法调度延迟回正命令，客户端为空: {PhotoelectricName}", photoelectricName);
            // 直接设置状态为重置，允许后续信号处理
            pendulumState.SetReset();
            return;
        }

        // 【最终修复】不再信任任何来自PendulumState的状态，只根据需要回正的格口号来决定回正方向。
        // 这是最可靠的事实来源。
        var directionForReset = ShouldSwingLeft(slotToResetFrom)
            ? PendulumDirection.SwingingLeft
            : PendulumDirection.SwingingRight;
        Log.Debug("根据上一个格口 {LastSlot} 确定回正方向为: {Direction}", slotToResetFrom, directionForReset);

        var resetCommand = directionForReset == PendulumDirection.SwingingLeft
            ? PendulumCommands.Module2.ResetLeft
            : PendulumCommands.Module2.ResetRight;

        var resetCmdBytes = GetCommandBytes(resetCommand);
        var resetDir = directionForReset == PendulumDirection.SwingingLeft ? "左" : "右";

        Log.Debug("准备调度延迟 {ResetDir} 回正命令 ({ResetCommand}) 到硬件命令调度器", resetDir, resetCommand);

        // 创建包含状态恢复逻辑的重试回调
        bool RetryCallbackWithStateReset(TcpClientService? tcpClient, byte[] cmdBytes, string deviceName)
        {
            try
            {
                var success = SendCommandWithRetryAsync(tcpClient, cmdBytes, deviceName);

                if (success)
                {
                    Log.Information("硬件调度器执行延迟 {ResetDir} 回正命令 ({ResetCommand}) 成功", resetDir, resetCommand);
                    pendulumState.SetReset();
                }
                else
                {
                    Log.Error("硬件调度器执行延迟 {ResetDir} 回正命令 ({ResetCommand}) 失败，强制复位状态", resetDir, resetCommand);
                    pendulumState.ForceReset();
                }

                return success;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "硬件调度器执行延迟回正命令时发生异常");
                pendulumState.ForceReset();
                return false;
            }
        }

        // 使用硬件命令调度器
        HardwareCommandScheduler.ScheduleDelayedReset(
            client,
            resetCommand,
            resetCmdBytes,
            photoelectricName,
            resetDelay,
            RetryCallbackWithStateReset
        );
    }

    /// <summary>
    ///     处理包裹超时
    /// </summary>
    /// <summary>
    ///     处理可分拣包裹的超时失败
    /// </summary>
    private void HandleSortTimeout(PackageInfo package, string photoelectricName)
    {
        var packageContext = $"[包裹{package.Index}|{package.Barcode}]";
        using (LogContext.PushProperty("PackageContext", packageContext))
        {
            // 1. 清理包裹自身的资源
            if (PendingSortPackages.TryRemove(package.Index, out var pkg))
            {
                pkg.SetSortState(PackageSortState.Error, "分拣超时");
                Log.Error("【分拣失败-超时】包裹 {Index}|{Barcode} 分拣超时，错过目标光电 '{PhotoelectricName}'。该包裹将直行至末端。", pkg.Index,
                    pkg.Barcode, photoelectricName);

                // 【新增】触发分拣完成事件，通知订阅者（如UI）
                SortingCompleted?.Invoke(this, pkg);
            }
        }
    }

    /// <summary>
    ///     处理直行包裹的超时（正常流程）
    /// </summary>
    private void HandleStraightThroughTimeout(PackageInfo package)
    {
        var packageContext = $"[包裹{package.Index}|{package.Barcode}]";
        using (LogContext.PushProperty("PackageContext", packageContext))
        {
            // 清理定时器
            if (PackageTimers.TryRemove(package.Index, out var timer)) timer.Dispose();

            // 直行包裹超时是正常流程
            if (PendingSortPackages.TryRemove(package.Index, out var pkg))
            {
                pkg.SetSortState(PackageSortState.Sorted);
                Log.Information("直行包裹超时，视为分拣成功。已从待处理队列移除。");

                // 【新增】触发分拣完成事件
                SortingCompleted?.Invoke(this, pkg);
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

            // 【新增】停止并释放所有等待定时器
            foreach (var timer in _pendulumWaitingTimers.Values)
            {
                timer.Stop();
                timer.Dispose();
            }

            _pendulumWaitingTimers.Clear();

            CancellationTokenSource?.Dispose();
            _sortingSignalQueue.Dispose();

            // 注意：硬件命令调度器是全局单例，不在这里停止
            // 调度器会在应用程序关闭时自动停止
            Log.Debug("BasePendulumSortService 托管资源已释放.");
        }

        // 释放非托管资源

        _disposed = true;
    }

    /// <summary>
    ///     【生产者】将分拣信号放入处理队列
    /// </summary>
    protected void EnqueueSortingSignal(string photoelectricName, DateTime signalTime)
    {
        if (!IsRunningFlag || _sortingSignalQueue.IsAddingCompleted) return;

        try
        {
            // 非阻塞添加，如果队列已满（理论上不应该），可以记录日志但不会阻塞TCP线程
            if (_sortingSignalQueue.TryAdd((photoelectricName, signalTime)))
                Log.Debug("分拣信号已入队: 光电 {Name}, 时间 {Time:HH:mm:ss.fff}", photoelectricName, signalTime);
            else
                Log.Warning("分拣信号队列已满或已关闭，无法添加信号: 光电 {Name}", photoelectricName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "将分拣信号入队时发生错误");
        }
    }

    /// <summary>
    ///     【消费者】启动后台任务以处理分拣信号队列
    /// </summary>
    protected void StartConsumer()
    {
        _signalConsumerTask = Task.Run(ProcessSortingSignalsLoop, CancellationTokenSource!.Token);
    }

    /// <summary>
    ///     【消费者】停止分拣信号处理任务
    /// </summary>
    protected async Task StopConsumer()
    {
        // 告知队列不再接受新项目
        if (!_sortingSignalQueue.IsAddingCompleted) _sortingSignalQueue.CompleteAdding();

        if (_signalConsumerTask != null)
        {
            Log.Debug("等待分拣信号消费者任务完成...");
            // 等待消费者处理完队列中剩余的项目
            await _signalConsumerTask;
            Log.Debug("分拣信号消费者任务已完成.");
        }
    }

    /// <summary>
    ///     【消费者】处理分拣信号的循环
    /// </summary>
    private void ProcessSortingSignalsLoop()
    {
        Log.Information("分拣信号处理消费者任务已启动.");
        try
        {
            // 使用 GetConsumingEnumerable 会在队列为空且被标记为完成后自动退出循环
            foreach (var (photoelectricName, signalTime) in _sortingSignalQueue.GetConsumingEnumerable(
                         CancellationTokenSource!.Token))
                try
                {
                    Log.Information("从队列中取出分拣信号，开始处理. 光电: {Name}", photoelectricName);

                    // 触发分拣光电信号事件
                    RaiseSortingPhotoelectricSignal(photoelectricName, signalTime);

                    // 使用基类的匹配逻辑
                    var package = MatchPackageForSorting(photoelectricName);
                    if (package == null)
                    {
                        Log.Debug("消费者任务：光电 {Name} 未匹配到包裹.", photoelectricName);

                        // 【修复】检查此信号是否中断了正在等待的摆轮
                        if (PendulumStates.TryGetValue(photoelectricName, out var pendulumState) &&
                            pendulumState.CurrentDirection == PendulumDirection.WaitingForNext)
                        {
                            Log.Warning("光电 {Name} 触发但未匹配到包裹，而其摆轮正处于等待状态。立即执行强制回正！", photoelectricName);
                            // 1. 解除看门狗
                            StopWaitingTimer(photoelectricName);
                            // 2. 立即发送回正命令
                            var client = GetSortingClient(photoelectricName);
                            if (client != null)
                            {
                                ExecuteImmediateReset(client, pendulumState, photoelectricName, "WaitingInterrupted");
                            }
                            else
                            {
                                Log.Error("无法获取客户端，对 {Name} 的中断回正失败！将强制软件复位。", photoelectricName);
                                pendulumState.ForceReset(); // 最后的保障
                            }
                        }

                        continue;
                    }

                    var matchTime = DateTime.Now;
                    var timeSinceTrigger = matchTime - package.TriggerTimestamp;

                    Log.Information("消费者任务：光电 {Name} 匹配到包裹 {Index}|{Barcode} (耗时: {MatchDuration:F2}ms)",
                        photoelectricName, package.Index, package.Barcode, timeSinceTrigger.TotalMilliseconds);

                    // 异步执行分拣动作
                    _ = ExecuteSortingAction(package, photoelectricName);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理单个分拣信号时发生错误. 光电: {Name}", photoelectricName);
                }
        }
        catch (OperationCanceledException)
        {
            Log.Information("分拣信号处理消费者任务已通过取消令牌正常停止.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "分拣信号处理消费者任务发生致命错误.");
        }
        finally
        {
            Log.Information("分拣信号处理消费者任务已结束.");
        }
    }

    /// <summary>
    ///     【新增】启动或更新用于等待下一个包裹的超时定时器
    /// </summary>
    private void StartOrUpdateWaitingTimer(string photoelectricName, PendulumState pendulumState, double timeoutMs)
    {
        // 先停止并移除旧的定时器
        StopWaitingTimer(photoelectricName);

        var timer = new Timer(timeoutMs) { AutoReset = false };
        timer.Elapsed += (_, _) =>
        {
            Log.Warning("摆轮 {Name} 等待下一个连续包裹超时 (持续 {TimeoutMs:F0}ms)，将执行强制回正",
                photoelectricName, timeoutMs);

            try
            {
                var client = GetSortingClient(photoelectricName);
                if (client != null)
                {
                    ExecuteImmediateReset(client, pendulumState, photoelectricName, "等待状态超时-专用定时器回正");
                }
                else
                {
                    Log.Error("【严重】摆轮 {Name} 等待超时回正失败：无法获取客户端连接", photoelectricName);
                    pendulumState.ForceReset();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "摆轮 {Name} 等待超时回正过程中发生异常", photoelectricName);
                pendulumState.ForceReset();
            }
            finally
            {
                // 超时后清理定时器
                StopWaitingTimer(photoelectricName);
            }
        };

        if (_pendulumWaitingTimers.TryAdd(photoelectricName, timer))
        {
            timer.Start();
            Log.Debug("已为摆轮 {Name} 启动等待超时定时器，超时时间: {TimeoutMs:F0}ms", photoelectricName, timeoutMs);
        }
    }

    /// <summary>
    ///     【新增】停止并移除等待超时定时器
    /// </summary>
    private void StopWaitingTimer(string photoelectricName)
    {
        if (_pendulumWaitingTimers.TryRemove(photoelectricName, out var timer))
        {
            timer.Stop();
            timer.Dispose();
            Log.Debug("已停止并移除摆轮 {Name} 的等待超时定时器", photoelectricName);
        }
    }

    /// <summary>
    ///     检查超时的包裹
    /// </summary>
    private void CheckTimeoutPackages(object? sender, ElapsedEventArgs e)
    {
        var now = DateTime.Now;

        // 检查处理中的包裹超时
        if (!ProcessingPackages.IsEmpty)
        {
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


        // 【新增】定期监控包裹状态分布（每2分钟一次）
        if (now.Minute % 2 == 0 && now.Second < 2) MonitorPackageStates();
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
            // 如果添加失败，说明可能并发冲突，记录警告
            Log.Warning("尝试标记包裹 {Barcode} 为处理中失败 (可能已被标记).", barcode);
        else
            Log.Debug("包裹 {Barcode} 已标记为由光电 {PhotoelectricId} 处理中.", barcode, photoelectricId);
    }

    /// <summary>
    ///     处理触发光电信号
    /// </summary>
    private void HandleTriggerPhotoelectric(string data)
    {
        // 检查是否为触发光电的特殊信号格式 010501#
        if (data.Contains("010501#"))
        {
            var triggerTime = DateTime.Now;
            Log.Information("收到触发光电特殊信号: {Signal}，记录触发时间: {TriggerTime:HH:mm:ss.fff}", data, triggerTime);

            // 通过 EventAggregator 发布触发光电信号事件
            try
            {
                _eventAggregator.GetEvent<TriggerSignalEvent>().Publish(triggerTime);
                Log.Debug("已通过 EventAggregator 发布 TriggerSignalEvent.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "通过 EventAggregator 发布 TriggerSignalEvent 时发生错误");
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
        else
        {
            // 处理其他格式的触发信号（兼容原有逻辑）
            var triggerTime = DateTime.Now;
            Log.Debug("收到触发信号: {Signal}，记录触发时间: {TriggerTime:HH:mm:ss.fff}", data, triggerTime);

            // 通过 EventAggregator 发布触发光电信号事件
            try
            {
                _eventAggregator.GetEvent<TriggerSignalEvent>().Publish(triggerTime);
                Log.Debug("已通过 EventAggregator 发布 TriggerSignalEvent.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "通过 EventAggregator 发布 TriggerSignalEvent 时发生错误");
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
        var config = SettingsService.LoadSettings<PendulumSortConfig>();
        var debounceTime = config.GlobalDebounceTime;

        foreach (var line in lines)
        {
            // 只处理高电平信号，忽略低电平和其他信号
            bool isHighLevelSignal;
            string signalType;

            // 【新增】特殊处理触发光电的 010501# 信号
            if (photoelectricName == "触发光电" && line.Contains("010501#"))
            {
                isHighLevelSignal = true;
                signalType = "触发光电特殊信号";
                Log.Information("触发光电收到特殊信号: {Signal}", line);
            }
            else if (line.Contains("OCCH1:1"))
            {
                isHighLevelSignal = true;
                signalType = "OCCH1高电平";
            }
            else if (line.Contains("OCCH2:1"))
            {
                isHighLevelSignal = true;
                signalType = "OCCH2高电平";
            }
            else if (line.Contains("OCCH1:0") || line.Contains("OCCH2:0"))
            {
                // 低电平信号，记录但不处理
                Log.Debug("光电 {PhotoelectricName} 收到低电平信号 '{SignalLine}'，已忽略", photoelectricName, line);

                // 【新增】验证低电平信号的完整性
                ValidateLowLevelSignal(photoelectricName, line);
                continue;
            }
            else
            {
                // 其他未知信号，直接忽略
                Log.Debug("光电 {PhotoelectricName} 收到未知信号 '{SignalLine}'，已忽略", photoelectricName, line);
                continue;
            }

            // 【新增】更新信号状态跟踪
            UpdateSignalStateTracking(photoelectricName, isHighLevelSignal, now);

            // 只处理高电平信号
            if (isHighLevelSignal)
            {
                // 检查防抖 - 只对高电平信号进行防抖检查
                if (LastSignalTimes.TryGetValue(photoelectricName, out var lastSignalTime))
                {
                    var elapsedSinceLastSignal = (now - lastSignalTime).TotalMilliseconds;
                    if (elapsedSinceLastSignal < debounceTime)
                    {
                        Log.Debug("光电 {PhotoelectricName} 在 {DebounceTime}ms 防抖时间内收到重复高电平信号 '{SignalLine}'，已忽略.",
                            photoelectricName, debounceTime, line);
                        continue; // 忽略此重复高电平信号
                    }
                }

                // 更新上次信号时间（只对高电平信号更新）
                LastSignalTimes[photoelectricName] = now;

                Log.Debug("光电 {PhotoelectricName} 收到有效高电平信号: {SignalType} - {SignalLine}",
                    photoelectricName, signalType, line);

                // 【新增】验证设备身份和信号来源
                ValidateSignalSource(photoelectricName, line, signalType);

                // 根据信号类型分发处理
                if (photoelectricName == "触发光电" && line.Contains("010501#"))
                    // 触发光电的特殊信号处理
                    HandleTriggerPhotoelectric(line);
                else if (line.Contains("OCCH1:1"))
                    HandleTriggerPhotoelectric(line);
                else if (line.Contains("OCCH2:1")) HandleSecondPhotoelectric(line);
            }
        }
    }

    /// <summary>
    ///     处理分拣信号并匹配包裹
    /// </summary>
    private PackageInfo? MatchPackageForSorting(string photoelectricName)
    {
        Log.Debug("分拣光电 {Name} 触发，开始匹配包裹...", photoelectricName);

        var currentTime = DateTime.Now;
        PackageInfo? matchedPackage = null;

        try
        {
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
                    Log.Information(
                        "🔍 检查包裹匹配条件 - 包裹条码: {Barcode}, 目标格口: {Chute}, 触发时间: {Timestamp:HH:mm:ss.fff}, 分拣状态: {SortState}",
                        pkg.Barcode, pkg.ChuteNumber, pkg.TriggerTimestamp, pkg.SortState);

                    // 【新增】详细的状态检查日志
                    Log.Debug("📋 包裹详细状态检查:");
                    Log.Debug("  - 触发时间戳: {TriggerTimestamp} (默认值: {IsDefault})",
                        pkg.TriggerTimestamp, pkg.TriggerTimestamp == default);
                    Log.Debug("  - 分拣状态: {SortState} (是否为Pending: {IsPending})",
                        pkg.SortState, pkg.SortState == PackageSortState.Pending);
                    Log.Debug("  - 是否已标记为处理中: {IsProcessing}",
                        IsPackageProcessing(pkg.Barcode));
                    Log.Debug("  - 定时器状态: {TimerEnabled}",
                        PackageTimers.TryGetValue(pkg.Index, out var pkgTimer) ? pkgTimer.Enabled : "无定时器");

                    // 使用统一的包裹验证方法
                    if (!IsPackageValidForProcessing(pkg, photoelectricName, currentTime))
                    {
                        Log.Information("❌ 匹配失败: 包裹未通过验证检查");
                        continue;
                    }

                    var delay = (currentTime - pkg.TriggerTimestamp).TotalMilliseconds;
                    Log.Information(
                        "⏱️ 时间差计算: 当前时间 {CurrentTime:HH:mm:ss.fff} - 触发时间 {TriggerTime:HH:mm:ss.fff} = {Delay:F1}ms",
                        currentTime, pkg.TriggerTimestamp, delay);
                    Log.Information("📏 时间范围检查: 延迟 {Delay:F1}ms, 允许范围 [{Lower:F1} - {Upper:F1}]ms, 结果: ✅ 符合",
                        delay, timeRangeLower, timeRangeUpper);

                    // 所有条件都满足，匹配成功
                    Log.Information("🎯 匹配成功! 包裹条码: {Barcode}, 格口: {Chute}, 延迟: {Delay:F1}ms, 光电: {PhotoelectricName}",
                        pkg.Barcode, pkg.ChuteNumber, delay, photoelectricName);

                    // 【新增】验证匹配的合理性
                    ValidatePackageMatching(pkg, photoelectricName, delay, timeRangeLower, timeRangeUpper);

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
            Log.Debug("包裹条码: {Barcode}, 匹配成功，已停止并移除超时定时器.", matchedPackage.Barcode);
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
        var sortConfig = SettingsService.LoadSettings<PendulumSortConfig>();
        var photoelectricConfig = sortConfig.SortingPhotoelectrics.FirstOrDefault(p => p.Name == photoelectricName);

        if (photoelectricConfig != null) return photoelectricConfig;

        // 如果在分拣光电中找不到，检查是否为触发光电 (适用于单摆轮)
        return photoelectricName is "触发光电" or "默认"
            ? sortConfig.TriggerPhotoelectric
            : throw
                // 都找不到则抛出异常
                new KeyNotFoundException($"无法找到名为 '{photoelectricName}' 的光电配置.");
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
    private async Task ExecuteSortingAction(PackageInfo package, string photoelectricName)
    {
        // --- 开始应用日志上下文 ---
        var packageContext = $"[包裹{package.Index}|{package.Barcode}]";
        using (LogContext.PushProperty("PackageContext", packageContext))
        {
            Log.Information("开始执行分拣动作 (光电: {PhotoelectricName}, 格口: {Chute}).", photoelectricName, package.ChuteNumber);
            PendulumState? pendulumState = null;

            try
            {
                var client = GetSortingClient(photoelectricName);
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
                await Task.Delay(sortDelay);

                // 3. 确定目标动作并发送摆动命令
                var targetSlot = package.ChuteNumber;
                var swingLeft = ShouldSwingLeft(targetSlot);
                var swingRight = ShouldSwingRight(targetSlot);
                var needsResetLater = false;

                // 【重构】摆动前状态检查：在发送物理摆动命令前进行最终状态检查
                var targetDirection = swingLeft ? PendulumDirection.SwingingLeft :
                    swingRight ? PendulumDirection.SwingingRight :
                    PendulumDirection.Reset;
                var currentDirection = pendulumState.CurrentDirection;

                Log.Debug("摆动前状态检查 - 目标方向: {TargetDirection}, 当前状态: {CurrentDirection}",
                    targetDirection, currentDirection);

                if (swingLeft || swingRight) // 包裹需要摆动
                {
                    // 【修改】改进状态匹配逻辑：当状态为WaitingForNext时，检查等待的格口是否与目标方向匹配
                    var shouldSkipSwing = false;
                    var skipReason = "";

                    if (currentDirection == PendulumDirection.WaitingForNext)
                    {
                        // 检查等待的格口是否与当前目标格口相同，且方向匹配
                        if (pendulumState.WaitingForSlot == targetSlot)
                        {
                            // 检查等待的格口是否需要相同的摆动方向
                            var waitingSlotNeedsLeftSwing = ShouldSwingLeft(pendulumState.WaitingForSlot);
                            var currentSlotNeedsLeftSwing = ShouldSwingLeft(targetSlot);

                            if (waitingSlotNeedsLeftSwing == currentSlotNeedsLeftSwing)
                            {
                                shouldSkipSwing = true;
                                skipReason =
                                    $"连续分拣优化：等待格口 {pendulumState.WaitingForSlot} 与目标格口 {targetSlot} 方向一致({(currentSlotNeedsLeftSwing ? "左摆" : "右摆")})";
                            }
                            else
                            {
                                skipReason = $"等待格口 {pendulumState.WaitingForSlot} 与目标格口 {targetSlot} 方向不一致，需要重新摆动";
                            }
                        }
                        else
                        {
                            skipReason = $"等待格口 {pendulumState.WaitingForSlot} 与目标格口 {targetSlot} 不同，需要重新摆动";
                        }
                    }
                    else if (targetDirection == currentDirection)
                    {
                        // 其他状态的连续分拣场景：目标方向与当前状态一致，跳过摆动命令
                        shouldSkipSwing = true;
                        skipReason = $"连续分拣优化：目标方向 {targetDirection} 与当前状态 {currentDirection} 一致";
                    }

                    if (shouldSkipSwing)
                    {
                        Log.Information("{SkipReason}，跳过重复的摆动命令", skipReason);
                        needsResetLater = true;

                        // 【新增】如果是因为匹配了等待中的格口而跳过摆动，则需要停止对应的等待超时定时器
                        if (currentDirection == PendulumDirection.WaitingForNext) StopWaitingTimer(photoelectricName);
                    }
                    else
                    {
                        // 状态不匹配：需要先回正再摆动到正确方向
                        Log.Information("状态不匹配：目标方向 {TargetDirection} 与当前状态 {CurrentDirection} 不一致，执行纠正流程",
                            targetDirection, currentDirection);

                        // 如果当前不是复位状态，先发送回正命令
                        if (currentDirection != PendulumDirection.Reset)
                        {
                            Log.Debug("当前摆轮不在复位状态，先执行回正");
                            ExecuteImmediateReset(client, pendulumState, photoelectricName, "摆动前状态检查-回正");

                            // 延迟20ms给硬件反应时间
                            await Task.Delay(20);
                        }

                        // 发送正确的摆动命令
                        var commandToSend = swingLeft
                            ? PendulumCommands.Module2.SwingLeft
                            : PendulumCommands.Module2.SwingRight;
                        var commandLogName = swingLeft ? "左摆" : "右摆";
                        needsResetLater = true;

                        Log.Debug("发送摆动命令: {CommandLogName} ({CommandToSend})", commandLogName, commandToSend);
                        var commandBytes = GetCommandBytes(commandToSend);

                        // 【新增】记录分拣操作详情，用于后续验证
                        RecordSortingOperation(package, photoelectricName, commandToSend);

                        if (!SendCommandWithRetryAsync(client, commandBytes, photoelectricName))
                        {
                            Log.Error("发送摆动命令 '{CommandLogName}' ({CommandToSend}) 失败", commandLogName, commandToSend);
                            // 命令发送失败
                            ProcessingPackages.TryRemove(package.Barcode, out _);
                            pendulumState.ForceReset();
                            return;
                        }

                        // 命令发送成功，更新状态
                        pendulumState.SetSwinging(swingLeft);
                        Log.Information("已发送摆动命令: {CommandLogName} ({CommandToSend}) 并更新状态为: {State}",
                            commandLogName, commandToSend, pendulumState.GetCurrentState());

                        // 【新增】验证命令发送与预期格口的匹配性
                        ValidateSortingCommand(package, photoelectricName, commandLogName, targetSlot);
                    }
                }
                else
                {
                    // 直行包裹：如果摆轮不在复位状态，发送回正命令
                    if (currentDirection != PendulumDirection.Reset)
                    {
                        Log.Information("直行包裹：摆轮当前状态为 {CurrentDirection}，发送回正命令确保复位", currentDirection);
                        ExecuteImmediateReset(client, pendulumState, photoelectricName, "直行包裹-确保复位");
                    }
                    else
                    {
                        Log.Debug("直行包裹：摆轮已在复位状态，无需操作");
                    }
                }

                PendulumState.UpdateLastSlot(targetSlot);

                // 4. 如果需要，执行延迟回正或智能回正
                if (needsResetLater)
                {
                    var nextPackage =
                        GetNextPendingPackageForSameSlot(targetSlot, package.TriggerTimestamp, package.Index);
                    if (nextPackage != null)
                    {
                        // 基于触发时间差计算动态等待时间
                        var timeDiff = (nextPackage.TriggerTimestamp - package.TriggerTimestamp).TotalMilliseconds;
                        var dynamicWaitTime = Math.Max(timeDiff + 100, 500); // 至少等待500ms，包含100ms误差

                        // 发现下一个包裹格口相同且时间间隔合适，跳过回正，设置等待状态
                        pendulumState.SetWaitingForNext(targetSlot, dynamicWaitTime);

                        // 【新增】启动或更新等待下一个包裹的超时定时器
                        StartOrUpdateWaitingTimer(photoelectricName, pendulumState, dynamicWaitTime);

                        Log.Information(
                            "连续分拣优化: 发现下一个包裹格口相同({Slot})，基于触发时间差({TimeDiff:F1}ms)设置动态等待时间({WaitTime:F1}ms)，跳过回正，等待下一个包裹 (包裹: {NextIndex})",
                            targetSlot, timeDiff, dynamicWaitTime, nextPackage.Index);


                        // 跳过回正，直接完成当前包裹处理
                        // 从待处理队列中移除包裹
                        if (PendingSortPackages.TryRemove(package.Index, out _))
                            Log.Debug("分拣动作完成，已从待处理队列移除 (智能回正-跳过回正)");

                        // 设置包裹分拣状态为已分拣
                        package.SetSortState(PackageSortState.Sorted);

                        // 【新增】触发分拣完成事件
                        SortingCompleted?.Invoke(this, package);

                        return;
                    }

                    // 正常执行回正逻辑 - 使用硬件命令调度器替代Task.Delay
                    Log.Debug("未找到合适的连续分拣包裹，执行正常回正流程");
                    var resetDelay = photoelectricConfig.ResetDelay;
                    Log.Debug("将回正命令调度到硬件命令调度器，延迟 {ResetDelay}ms", resetDelay);

                    // 标记摆轮进入回正延迟状态，阻止新信号处理
                    pendulumState.SetResetting();

                    // 【核心修改】使用硬件命令调度器替代Task.Delay，避免线程饥饿
                    ScheduleDelayedResetCommand(client, pendulumState, photoelectricName, resetDelay,
                        package.ChuteNumber);
                }

                // 从待处理队列中移除包裹
                if (PendingSortPackages.TryRemove(package.Index, out _))
                    Log.Debug("分拣动作完成，已从待处理队列移除. {NeedsReset}",
                        needsResetLater ? "已处理回正" : "无需回正");
                else
                    Log.Warning("尝试移除已完成的包裹失败 (可能已被移除).");

                // 设置包裹分拣状态为已分拣
                package.SetSortState(PackageSortState.Sorted);
                Log.Debug("包裹分拣状态已更新为: Sorted");

                // 【新增】触发分拣完成事件
                SortingCompleted?.Invoke(this, package);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "执行分拣动作时发生异常.");
                PendingSortPackages.TryRemove(package.Index, out _);

                // 【修复】异常时尝试发送物理回正命令，而不仅仅是软件复位
                if (pendulumState != null)
                {
                    Log.Warning("由于异常，将尝试发送物理回正命令以确保摆轮状态正确");

                    // 在后台线程执行回正，避免阻塞异常处理LOB
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var exceptionClient = GetSortingClient(photoelectricName);
                            if (exceptionClient != null && exceptionClient.IsConnected() &&
                                pendulumState.CurrentDirection != PendulumDirection.Reset)
                                ExecuteImmediateReset(exceptionClient, pendulumState, photoelectricName,
                                    "ExceptionRecovery");
                            else
                                pendulumState.ForceReset(); // 如果无法发送命令，至少软件复位
                        }
                        catch (Exception resetEx)
                        {
                            Log.Error(resetEx, "异常恢复时执行回正操作失败");
                            pendulumState.ForceReset(); // 最后的兜底操作
                        }
                    }).ContinueWith(t =>
                    {
                        if (t is { IsFaulted: true, Exception: not null })
                            Log.Warning(t.Exception, "摆轮 {Name} 异常恢复回正任务发生未观察的异常", photoelectricName);
                    }, TaskContinuationOptions.OnlyOnFaulted);
                }

                // 异常情况下也要更新状态，避免包裹卡在Processing状态
                package.SetSortState(PackageSortState.Error, "分拣异常");
                Log.Error("【分拣失败-异常】包裹 {Index}|{Barcode} 在执行分拣动作时发生异常，状态已设为Error.", package.Index, package.Barcode);

                // 【新增】触发分拣完成事件，通知UI
                SortingCompleted?.Invoke(this, package);
            }
            finally
            {
                if (ProcessingPackages.TryRemove(package.Barcode, out _))
                    Log.Debug("已从处理中状态移除.");
                else
                    Log.Warning("尝试从处理中状态移除失败 (可能已被移除).");
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
    ///     发送命令（单次发送，不重试）
    /// </summary>
    private bool SendCommandWithRetryAsync(TcpClientService? client, byte[] command,
        string photoelectricName)
    {
        var commandString = Encoding.ASCII.GetString(command).Trim(); // 用于日志记录
        Log.Debug("准备向 {Name} 发送命令: {Command}", photoelectricName, commandString);

        // 检查连接状态，如果未连接则直接返回失败
        if (client != null && !client.IsConnected())
        {
            Log.Warning("客户端 {Name} 未连接，无法发送命令 {Command}", photoelectricName, commandString);
            UpdateDeviceConnectionState(photoelectricName, false);
            return false;
        }

        // 单次发送命令
        try
        {
            client?.Send(command);
            Log.Debug("命令 {Command} 已成功发送到 {Name}", commandString, photoelectricName);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "发送命令 {Command} 到 {Name} 失败", commandString, photoelectricName);
            UpdateDeviceConnectionState(photoelectricName, false); // 标记为断开
            return false;
        }
    }

    /// <summary>
    ///     验证包裹是否可以被处理
    /// </summary>
    /// <param name="package">要验证的包裹</param>
    /// <param name="photoelectricName">光电名称（可选，用于格口匹配检查）</param>
    /// <param name="currentTime">当前时间</param>
    /// <returns>包裹是否可以被处理</returns>
    private bool IsPackageValidForProcessing(PackageInfo package, string? photoelectricName = null,
        DateTime? currentTime = null)
    {
        // 【新增】详细的状态检查日志
        Log.Debug("🔍 包裹 {Index}|{Barcode} 详细验证检查:", package.Index, package.Barcode);

        // 基本条件检查
        if (package.TriggerTimestamp == default)
        {
            Log.Debug("包裹 {Index}|{Barcode} 匹配失败: 触发时间戳无效.", package.Index, package.Barcode);
            return false;
        }

        // 只处理待处理状态的包裹
        if (package.SortState != PackageSortState.Pending)
        {
            Log.Debug("包裹 {Index}|{Barcode} 匹配失败: 分拣状态为 {SortState}，非待处理.", package.Index, package.Barcode,
                package.SortState);
            return false;
        }

        // 如果指定了光电名称，检查格口是否属于该光电
        if (photoelectricName != null && !SlotBelongsToPhotoelectric(package.ChuteNumber, photoelectricName))
        {
            Log.Debug("包裹 {Index}|{Barcode} 匹配失败: 格口 {ChuteNumber} 不属于光电 {PhotoelectricName}.", package.Index,
                package.Barcode, package.ChuteNumber, photoelectricName);
            return false;
        }

        // 检查包裹是否已标记为处理中
        if (IsPackageProcessing(package.Barcode))
        {
            Log.Debug("包裹 {Index}|{Barcode} 匹配失败: 包裹已标记为处理中.", package.Index, package.Barcode);
            return false;
        }

        // 检查是否已超时 (基于 Timer 状态)
        if (PackageTimers.TryGetValue(package.Index, out var timer) && !timer.Enabled)
        {
            Log.Debug("包裹 {Index}|{Barcode} 匹配失败: 包裹计时器已停止 (可能已超时).", package.Index, package.Barcode);
            return false;
        }

        // 如果提供了光电名称和当前时间，进行时间窗口检查
        if (photoelectricName != null && currentTime.HasValue)
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

            var delay = (currentTime.Value - package.TriggerTimestamp).TotalMilliseconds;
            const double tolerance = 10.0;
            var delayInRange = delay >= timeRangeLower - tolerance && delay <= timeRangeUpper + tolerance;

            if (!delayInRange)
            {
                Log.Information(
                    "包裹 {Index}|{Barcode} 匹配失败: 时间范围不匹配. 时间差: {Delay:F1}ms, 允许范围: [{Lower:F1} - {Upper:F1}]ms.",
                    package.Index, package.Barcode, delay, timeRangeLower, timeRangeUpper);
                return false;
            }
        }

        // 【新增】验证通过日志
        Log.Debug("✅ 包裹 {Index}|{Barcode} 验证通过，可以进行分拣处理", package.Index, package.Barcode);
        return true;
    }

    /// <summary>
    ///     查找下一个相同格口的待处理包裹，并基于触发时间差动态计算等待时间
    /// </summary>
    /// <param name="currentSlot">当前格口号</param>
    /// <param name="currentPackageTriggerTime">当前包裹的触发时间</param>
    /// <param name="currentPackageIndex">当前包裹的序号</param>
    /// <returns>下一个相同格口的包裹，如果没有或时间间隔不合适则返回null</returns>
    private PackageInfo? GetNextPendingPackageForSameSlot(int currentSlot, DateTime? currentPackageTriggerTime = null,
        int? currentPackageIndex = null)
    {
        PackageInfo? nextPackage;

        // 如果提供了当前包裹的Index，优先查找Index+1的包裹（真正的连续包裹）
        if (currentPackageIndex.HasValue)
        {
            var expectedNextIndex = currentPackageIndex.Value + 1;
            nextPackage = PendingSortPackages.Values
                .FirstOrDefault(p => p.Index == expectedNextIndex &&
                                     p.ChuteNumber == currentSlot &&
                                     IsPackageValidForProcessing(p));

            if (nextPackage != null)
            {
                Log.Debug("找到真正连续的分拣包裹，序号: {ExpectedIndex}，格口: {ChuteNumber}，包裹: {Index}",
                    expectedNextIndex, currentSlot, nextPackage.Index);
            }
            else
            {
                Log.Debug("未找到序号为 {ExpectedIndex} 的连续包裹（格口: {ChuteNumber}），跳过连续分拣优化.",
                    expectedNextIndex, currentSlot);
                return null;
            }
        }
        else
        {
            // 如果没有提供当前包裹Index，使用原有逻辑作为fallback（按索引排序的第一个待处理包裹）
            nextPackage = PendingSortPackages.Values
                .Where(p => IsPackageValidForProcessing(p)) // 使用统一的验证方法
                .OrderBy(p => p.Index)
                .FirstOrDefault();

            // 如果下一个包裹不存在或格口不同，直接返回null
            if (nextPackage == null || nextPackage.ChuteNumber != currentSlot)
            {
                if (nextPackage == null)
                    Log.Debug("未找到下一个待处理包裹用于连续分拣.");
                else
                    Log.Debug("找到下一个待处理包裹 {Index}|{Barcode}，但格口 {NextChute} 与当前格口 {CurrentChute} 不符.",
                        nextPackage.Index, nextPackage.Barcode, nextPackage.ChuteNumber, currentSlot);
                return null;
            }
        }

        // 如果提供了当前包裹的触发时间，进行时间间隔检查
        if (currentPackageTriggerTime.HasValue && nextPackage.TriggerTimestamp != default)
        {
            var timeDiff = (nextPackage.TriggerTimestamp - currentPackageTriggerTime.Value).TotalMilliseconds;
            const double toleranceMs = 100.0; // 允许100ms误差

            // 获取配置的最大连续分拣间隔作为上限
            var config = SettingsService.LoadSettings<PendulumSortConfig>();
            var maxIntervalMs = config.ContinuousSortMaxIntervalMs;

            if (timeDiff < 0 || timeDiff > maxIntervalMs + toleranceMs)
            {
                Log.Debug(
                    "下一个包裹 {Index}|{Barcode} 时间间隔不合适: {TimeDiff:F1}ms (允许范围: 0 - {MaxInterval}ms + {Tolerance}ms误差)，不进行连续分拣.",
                    nextPackage.Index, nextPackage.Barcode, timeDiff, maxIntervalMs, toleranceMs);
                return null;
            }

            Log.Information(
                "连续分拣时间检查通过: 当前包裹触发时间 {CurrentTime:HH:mm:ss.fff}，下一个包裹触发时间 {NextTime:HH:mm:ss.fff}，时间差 {TimeDiff:F1}ms (在允许范围内)",
                currentPackageTriggerTime.Value, nextPackage.TriggerTimestamp, timeDiff);
        }

        Log.Debug("找到连续分拣包裹，格口: {ChuteNumber}，包裹: {Index}",
            currentSlot, nextPackage.Index);
        return nextPackage;
    }


    /// <summary>
    ///     执行立即回正（用于强制同步）
    /// </summary>
    private void ExecuteImmediateReset(TcpClientService client, PendulumState pendulumState, string photoelectricName,
        string reason)
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
            // 【BUG修复】不再信任来自 PendulumState 的当前方向，因为它在 WaitingForNext 状态下是模糊的。
            // 而是根据上一个处理的格口号来可靠地确定回正方向。
            // 这是最可靠的事实来源，与 ScheduleDelayedResetCommand 中的逻辑保持一致。
            var lastSlot = PendulumState.LastSlot;
            if (lastSlot == 0)
            {
                Log.Warning("无法执行立即回正，因为没有记录上一个格口号。将执行强制软件复位。");
                pendulumState.ForceReset();
                return;
            }

            var directionForReset = ShouldSwingLeft(lastSlot)
                ? PendulumDirection.SwingingLeft
                : PendulumDirection.SwingingRight;
            Log.Debug("根据上一个格口 {LastSlot} 确定立即回正方向为: {Direction}", lastSlot, directionForReset);

            // 根据计算出的方向发送对应的回正命令
            var resetCommand = directionForReset == PendulumDirection.SwingingLeft
                ? PendulumCommands.Module2.ResetLeft
                : PendulumCommands.Module2.ResetRight;

            var commandBytes = GetCommandBytes(resetCommand);
            var resetDirection = directionForReset == PendulumDirection.SwingingLeft ? "左" : "右";

            Log.Debug("发送 {Direction} 回正命令 ({Command})...", resetDirection, resetCommand);

            if (SendCommandWithRetryAsync(client, commandBytes, photoelectricName))
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
    private void RaiseSortingPhotoelectricSignal(string photoelectricName, DateTime signalTime)
    {
        try
        {
            _eventAggregator.GetEvent<SortingSignalEvent>().Publish((photoelectricName, signalTime));
            Log.Debug("已通过 EventAggregator 发布 SortingSignalEvent.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "通过 EventAggregator 发布 SortingSignalEvent 时发生错误，光电: {PhotoelectricName}", photoelectricName);
        }
    }

    /// <summary>
    ///     更新光电信号状态跟踪
    /// </summary>
    private void UpdateSignalStateTracking(string photoelectricName, bool isHighLevel, DateTime signalTime)
    {
        var state = _signalStates.GetOrAdd(photoelectricName, _ => new PhotoelectricSignalState());

        if (isHighLevel)
        {
            state.LastHighSignalTime = signalTime;
            state.TotalHighSignals++;

            if (state.LastSignalWasHigh)
            {
                state.ConsecutiveHighCount++;
                Log.Warning("【信号异常】光电 {Name} 连续收到 {Count} 次高电平信号，可能存在信号异常!",
                    photoelectricName, state.ConsecutiveHighCount);
            }
            else
            {
                state.ConsecutiveHighCount = 1;
                state.ConsecutiveLowCount = 0;
            }

            state.LastSignalWasHigh = true;
        }
        else
        {
            state.LastLowSignalTime = signalTime;
            state.TotalLowSignals++;

            if (!state.LastSignalWasHigh)
            {
                state.ConsecutiveLowCount++;
                Log.Warning("【信号异常】光电 {Name} 连续收到 {Count} 次低电平信号，可能存在信号异常!",
                    photoelectricName, state.ConsecutiveLowCount);
            }
            else
            {
                state.ConsecutiveLowCount = 1;
                state.ConsecutiveHighCount = 0;
            }

            state.LastSignalWasHigh = false;
        }

        // 检查信号异常
        if (state.IsSignalAbnormal())
            Log.Error("【信号严重异常】光电 {Name} 信号异常：连续高电平 {HighCount} 次，连续低电平 {LowCount} 次",
                photoelectricName, state.ConsecutiveHighCount, state.ConsecutiveLowCount);

        // 检查高低电平信号不匹配
        if (state.HasSignalMismatch())
            Log.Warning("【信号不匹配】光电 {Name} 高低电平信号数量不匹配：高电平 {HighTotal} 次，低电平 {LowTotal} 次，差值 {Diff}",
                photoelectricName, state.TotalHighSignals, state.TotalLowSignals,
                Math.Abs(state.TotalHighSignals - state.TotalLowSignals));

        // 定期输出信号统计
        var totalSignals = state.TotalHighSignals + state.TotalLowSignals;
        if (totalSignals > 0 && totalSignals % 100 == 0)
            Log.Information("【信号统计】光电 {Name} 累计接收信号: 高电平 {High} 次，低电平 {Low} 次，总计 {Total} 次",
                photoelectricName, state.TotalHighSignals, state.TotalLowSignals, totalSignals);
    }

    /// <summary>
    ///     验证信号源身份和合法性
    /// </summary>
    private void ValidateSignalSource(string photoelectricName, string signalData, string signalType)
    {
        try
        {
            // 检查信号格式是否符合预期
            var isValidFormat = signalData.Contains("OCCH1:") || signalData.Contains("OCCH2:");
            if (!isValidFormat)
            {
                Log.Warning("【信号格式异常】光电 {Name} 收到格式异常的信号: '{Signal}'", photoelectricName, signalData);
                return;
            }

            // 验证光电名称和信号类型的对应关系
            if (photoelectricName.Contains("触发"))
            {
                if (signalData.Contains("OCCH2:1"))
                    Log.Information("【触发光电】{Name} 收到 OCCH2 高电平信号，这通常用于分拣触发", photoelectricName);
                else if (signalData.Contains("OCCH1:1"))
                    Log.Information("【触发光电】{Name} 收到 OCCH1 高电平信号，这通常用于包裹检测", photoelectricName);
            }
            else if (photoelectricName.Contains("光电"))
            {
                Log.Information("【分拣光电】{Name} 收到 {SignalType} 信号: '{Signal}'",
                    photoelectricName, signalType, signalData.Trim());

                // 检查分拣光电是否收到了意外的信号类型
                if (signalData.Contains("OCCH1:1"))
                    Log.Debug("【分拣光电验证】{Name} 收到 OCCH1 信号，确认这是预期的分拣信号", photoelectricName);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "验证信号源时发生错误，光电: {Name}", photoelectricName);
        }
    }

    /// <summary>
    ///     记录分拣操作详情
    /// </summary>
    private void RecordSortingOperation(PackageInfo package, string photoelectricName, string command)
    {
        Log.Information("【分拣记录】包裹 {Index}|{Barcode} - 预期格口: {ExpectedChute}, 处理光电: {Photoelectric}, 发送命令: {Command}",
            package.Index, package.Barcode, package.ChuteNumber, photoelectricName, command);
    }


    /// <summary>
    ///     验证分拣命令与预期格口的匹配性
    /// </summary>
    private void ValidateSortingCommand(PackageInfo package, string photoelectricName, string commandName,
        int targetSlot)
    {
        try
        {
            // 验证光电与格口的对应关系
            var expectedPhotoelectric = GetPhotoelectricNameBySlot(targetSlot);
            if (expectedPhotoelectric != null && expectedPhotoelectric != photoelectricName)
                Log.Error(
                    "【分拣逻辑错误】包裹 {Index}|{Barcode} 目标格口 {TargetSlot} 应由光电 '{ExpectedPhotoelectric}' 处理，但实际由 '{ActualPhotoelectric}' 处理!",
                    package.Index, package.Barcode, targetSlot, expectedPhotoelectric, photoelectricName);

            // 验证摆动方向与格口的对应关系
            var shouldSwingLeft = targetSlot % 2 == 1; // 奇数格口左摆
            var shouldSwingRight = targetSlot % 2 == 0; // 偶数格口右摆

            if (shouldSwingLeft && !commandName.Contains("左"))
                Log.Error("【摆动方向错误】包裹 {Index}|{Barcode} 目标格口 {TargetSlot}(奇数) 应该左摆，但发送了 '{CommandName}' 命令!",
                    package.Index, package.Barcode, targetSlot, commandName);
            else if (shouldSwingRight && !commandName.Contains("右"))
                Log.Error("【摆动方向错误】包裹 {Index}|{Barcode} 目标格口 {TargetSlot}(偶数) 应该右摆，但发送了 '{CommandName}' 命令!",
                    package.Index, package.Barcode, targetSlot, commandName);
            else
                Log.Debug("【分拣验证通过】包裹 {Index}|{Barcode} 格口 {TargetSlot} 摆动方向 '{CommandName}' 正确",
                    package.Index, package.Barcode, targetSlot, commandName);

            // 验证格口范围
            if (targetSlot < 1)
                Log.Error("【格口异常】包裹 {Index}|{Barcode} 目标格口 {TargetSlot} 无效（小于1）!",
                    package.Index, package.Barcode, targetSlot);

            // 记录详细的分拣映射信息
            Log.Information("【分拣映射验证】包裹 {Index}|{Barcode}: 格口{TargetSlot} → 光电'{Photoelectric}' → 命令'{Command}'",
                package.Index, package.Barcode, targetSlot, photoelectricName, commandName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "验证分拣命令时发生错误，包裹: {Index}|{Barcode}", package.Index, package.Barcode);
        }
    }

    /// <summary>
    ///     验证低电平信号的完整性
    /// </summary>
    private void ValidateLowLevelSignal(string photoelectricName, string signalData)
    {
        try
        {
            // 更新信号状态跟踪（低电平）
            UpdateSignalStateTracking(photoelectricName, false, DateTime.Now);

            // 检查是否有对应的高电平信号
            if (_signalStates.TryGetValue(photoelectricName, out var state))
            {
                var timeSinceLastHigh = DateTime.Now - state.LastHighSignalTime;

                // 如果低电平信号出现但没有对应的高电平信号，可能有问题
                if (state.LastHighSignalTime == default)
                    Log.Warning("【信号完整性问题】光电 {Name} 收到低电平信号 '{Signal}'，但没有记录到对应的高电平信号",
                        photoelectricName, signalData.Trim());
                else if (timeSinceLastHigh.TotalMilliseconds > 5000) // 超过5秒没有高电平
                    Log.Warning("【信号时序异常】光电 {Name} 收到低电平信号，但距离上次高电平信号已过 {Time:F1}ms",
                        photoelectricName, timeSinceLastHigh.TotalMilliseconds);
                else
                    Log.Verbose("【信号配对正常】光电 {Name} 高低电平信号配对正常，间隔 {Time:F1}ms",
                        photoelectricName, timeSinceLastHigh.TotalMilliseconds);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "验证低电平信号时发生错误，光电: {Name}", photoelectricName);
        }
    }

    /// <summary>
    ///     验证包裹匹配的合理性
    /// </summary>
    private void ValidatePackageMatching(PackageInfo package, string photoelectricName, double delay,
        double timeRangeLower, double timeRangeUpper)
    {
        try
        {
            // 检查延迟时间是否在正常范围内
            var normalDelayRange = (timeRangeLower + timeRangeUpper) / 2;
            var delayDeviation = Math.Abs(delay - normalDelayRange);
            var maxDeviation = (timeRangeUpper - timeRangeLower) / 4; // 允许1/4范围的偏差

            if (delayDeviation > maxDeviation)
                Log.Warning(
                    "【时间偏差异常】包裹 {Index}|{Barcode} 延迟时间 {Delay:F1}ms 偏离正常值 {Normal:F1}ms 较大，偏差: {Deviation:F1}ms",
                    package.Index, package.Barcode, delay, normalDelayRange, delayDeviation);


            // 验证格口与光电的对应关系
            var expectedPhotoelectric = GetPhotoelectricNameBySlot(package.ChuteNumber);
            if (expectedPhotoelectric != photoelectricName && expectedPhotoelectric != null)
                Log.Error(
                    "【匹配逻辑严重错误】包裹 {Index}|{Barcode} 格口 {Chute} 被错误的光电 '{ActualPhotoelectric}' 匹配，应该由 '{ExpectedPhotoelectric}' 匹配!",
                    package.Index, package.Barcode, package.ChuteNumber, photoelectricName, expectedPhotoelectric);

            Log.Debug("【匹配验证】包裹 {Index}|{Barcode} 匹配验证完成：延迟 {Delay:F1}ms，光电 '{Photoelectric}'，格口 {Chute}",
                package.Index, package.Barcode, delay, photoelectricName, package.ChuteNumber);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "验证包裹匹配时发生错误，包裹: {Index}|{Barcode}", package.Index, package.Barcode);
        }
    }


    /// <summary>
    ///     【新增】监控包裹状态分布，用于诊断状态问题
    /// </summary>
    private void MonitorPackageStates()
    {
        try
        {
            if (PendingSortPackages.IsEmpty)
            {
                Log.Debug("📊 包裹状态监控: 待处理队列为空");
                return;
            }

            var stateGroups = PendingSortPackages.Values
                .GroupBy(p => p.SortState)
                .ToDictionary(g => g.Key, g => g.Count());

            Log.Information("📊 包裹状态分布监控:");
            foreach (var (state, count) in stateGroups) Log.Information("  - {State}: {Count} 个包裹", state, count);

            // 检查异常状态
            var nonPendingCount = stateGroups
                .Where(kv => kv.Key != PackageSortState.Pending)
                .Sum(kv => kv.Value);

            if (nonPendingCount > 0)
            {
                Log.Warning("⚠️ 发现 {Count} 个非待处理状态的包裹在待处理队列中", nonPendingCount);

                // 详细列出异常包裹
                var abnormalPackages = PendingSortPackages.Values
                    .Where(p => p.SortState != PackageSortState.Pending)
                    .Take(5) // 只显示前5个
                    .ToList();

                foreach (var pkg in abnormalPackages)
                    Log.Warning("  - 包裹 {Index}|{Barcode}: 状态={State}, 触发时间={TriggerTime:HH:mm:ss.fff}",
                        pkg.Index, pkg.Barcode, pkg.SortState, pkg.TriggerTimestamp);

                if (abnormalPackages.Count < PendingSortPackages.Count -
                    stateGroups.GetValueOrDefault(PackageSortState.Pending, 0)) Log.Warning("  ... 还有更多异常包裹未显示");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "监控包裹状态时发生错误");
        }
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
        public static PendulumCommands Module2 =>
            new()
            {
                Start = "AT+STACH1=1",
                Stop = "AT+STACH1=0",
                SwingLeft = "AT+STACH3=1",
                ResetLeft = "AT+STACH3=0",
                SwingRight = "AT+STACH2=1",
                ResetRight = "AT+STACH2=0"
            };

        public string Start { get; private init; }
        public string Stop { get; private init; }
        public string SwingLeft { get; private init; }
        public string ResetLeft { get; private init; }
        public string SwingRight { get; private init; }
        public string ResetRight { get; private init; }
    }

    /// <summary>
    ///     摆轮方向枚举
    /// </summary>
    protected enum PendulumDirection
    {
        Reset, // 复位状态
        SwingingLeft, // 左摆状态
        SwingingRight, // 右摆状态
        Resetting, // 回正延迟中（阻止新信号处理）
        WaitingForNext // 等待下一个相同格口包裹（智能回正）
    }

    /// <summary>
    ///     摆轮状态类
    /// </summary>
    protected class PendulumState
    {
        /// <summary>
        ///     【修复】记录最后一次操作的格口号，作为最可靠的事实来源
        /// </summary>
        public static int LastSlot { get; private set; }

        /// <summary>
        ///     获取当前摆轮方向
        /// </summary>
        public PendulumDirection CurrentDirection { get; private set; } = PendulumDirection.Reset;

        /// <summary>
        ///     进入回正延迟状态前的上一个摆轮方向，用于确定正确的回正命令
        /// </summary>
        private PendulumDirection PreviousDirection { get; set; } = PendulumDirection.Reset;

        /// <summary>
        ///     等待的目标格口号
        /// </summary>
        public int WaitingForSlot { get; private set; }

        /// <summary>
        ///     进入等待状态的时间戳，用于超时监控
        /// </summary>
        private DateTime? WaitingStartTime { get; set; }

        /// <summary>
        ///     动态计算的等待超时时间（毫秒）
        /// </summary>
        private double DynamicWaitTimeoutMs { get; set; }

        /// <summary>
        ///     设置摆动状态
        /// </summary>
        /// <param name="swingLeft">true表示左摆，false表示右摆</param>
        public void SetSwinging(bool swingLeft)
        {
            PreviousDirection = CurrentDirection; // 保存之前的状态
            CurrentDirection = swingLeft ? PendulumDirection.SwingingLeft : PendulumDirection.SwingingRight;
            WaitingStartTime = null; // 清除等待时间戳
            Log.Debug("摆轮状态更新为: {Direction}", CurrentDirection);
        }

        /// <summary>
        ///     设置复位状态
        /// </summary>
        public void SetReset()
        {
            PreviousDirection = CurrentDirection; // 保存之前的状态
            CurrentDirection = PendulumDirection.Reset;
            WaitingStartTime = null; // 清除等待时间戳
            Log.Debug("摆轮状态更新为: Reset");
        }

        /// <summary>
        ///     设置回正延迟状态，并保存当前摆轮方向用于后续回正
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
            WaitingStartTime = null; // 清除等待时间戳
            Log.Debug("摆轮状态更新为: Resetting (回正延迟中)");
        }

        /// <summary>
        ///     强制设置复位状态
        /// </summary>
        public void ForceReset()
        {
            PreviousDirection = CurrentDirection; // 保存之前的状态
            CurrentDirection = PendulumDirection.Reset;
            WaitingStartTime = null; // 清除等待时间戳
            Log.Debug("摆轮状态被强制复位");
        }

        /// <summary>
        ///     获取需要回正的方向（用于确定回正命令）
        /// </summary>
        /// <returns>需要回正的摆轮方向</returns>
        public PendulumDirection GetDirectionForReset()
        {
            // 如果当前是Resetting状态，使用之前保存的方向
            // 否则使用当前方向
            return CurrentDirection == PendulumDirection.Resetting ? PreviousDirection : CurrentDirection;
        }

        /// <summary>
        ///     更新最后处理的格口号
        /// </summary>
        public static void UpdateLastSlot(int slot)
        {
            LastSlot = slot; // 【修复】实际更新静态属性
            Log.Debug("更新最后处理的格口为: {Slot}", slot);
        }

        /// <summary>
        ///     设置等待下一个相同格口包裹状态
        /// </summary>
        /// <param name="slotNumber">等待的格口号</param>
        /// <param name="dynamicWaitTimeMs">动态计算的等待超时时间（毫秒），如果不提供则使用配置值</param>
        public void SetWaitingForNext(int slotNumber, double? dynamicWaitTimeMs = null)
        {
            if (CurrentDirection != PendulumDirection.Resetting) PreviousDirection = CurrentDirection;
            CurrentDirection = PendulumDirection.WaitingForNext;
            WaitingForSlot = slotNumber;
            WaitingStartTime = DateTime.Now; // 记录进入等待状态的时间
            DynamicWaitTimeoutMs = dynamicWaitTimeMs ?? 3000; // 默认3秒
            Log.Debug("摆轮状态更新为: WaitingForNext，等待格口: {Slot}，开始时间: {StartTime}，动态等待超时: {Timeout}ms",
                slotNumber, WaitingStartTime, DynamicWaitTimeoutMs);
        }

        /// <summary>
        ///     获取当前状态的字符串表示
        /// </summary>
        public string GetCurrentState()
        {
            return CurrentDirection switch
            {
                PendulumDirection.Reset => "Reset",
                PendulumDirection.SwingingLeft => "SwingingLeft",
                PendulumDirection.SwingingRight => "SwingingRight",
                PendulumDirection.Resetting => "Resetting",
                PendulumDirection.WaitingForNext => "WaitingForNext",
                _ => "Unknown"
            };
        }
    }

    /// <summary>
    ///     光电信号状态跟踪类，用于验证信号完整性
    /// </summary>
    private class PhotoelectricSignalState
    {
        public DateTime LastHighSignalTime { get; set; }
        public DateTime LastLowSignalTime { [UsedImplicitly] get; set; }
        public bool LastSignalWasHigh { get; set; }
        public int ConsecutiveHighCount { get; set; }
        public int ConsecutiveLowCount { get; set; }
        public int TotalHighSignals { get; set; }
        public int TotalLowSignals { get; set; }

        /// <summary>
        ///     检查信号是否异常（例如连续多次相同信号）
        /// </summary>
        public bool IsSignalAbnormal()
        {
            return ConsecutiveHighCount > 3 || ConsecutiveLowCount > 3;
        }

        /// <summary>
        ///     检查高低电平信号是否不匹配
        /// </summary>
        public bool HasSignalMismatch()
        {
            return Math.Abs(TotalHighSignals - TotalLowSignals) > 2;
        }
    }
}