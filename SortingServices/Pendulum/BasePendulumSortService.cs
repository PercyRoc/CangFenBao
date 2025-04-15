using System.Collections.Concurrent;
using System.Text;
using System.Timers;
using Common.Models.Package;
using Common.Models.Settings.Sort.PendulumSort;
using Common.Services.Settings;
using Serilog;
using SortingServices.Common;
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
    protected PendulumSortConfig Configuration = new();
    protected bool IsRunningFlag;
    protected TcpClientService? TriggerClient;

    protected BasePendulumSortService(ISettingsService settingsService)
    {
        _settingsService = settingsService;

        // 初始化超时检查定时器
        TimeoutCheckTimer = new Timer(5000); // 5秒检查一次
        TimeoutCheckTimer.Elapsed += CheckTimeoutPackages;
        TimeoutCheckTimer.AutoReset = true;

        // 订阅配置变更
        _settingsService.OnSettingsChanged<PendulumSortConfig>(HandleConfigurationChanged);
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
        if (!IsRunningFlag)
        {
            Log.Warning("分拣服务未运行，无法处理包裹 {Barcode}", package.Barcode);
            return;
        }

        if (IsPackageProcessing(package.Barcode))
        {
            Log.Warning("包裹 {Barcode} 已在处理中", package.Barcode);
            return;
        }

        // 如果包裹已经有关联的触发时间戳，则跳过匹配逻辑
        if (package.TriggerTimestamp != default)
        {
            Log.Information("包裹 {Barcode}(序号:{Index}) 已有关联的触发时间戳 {Timestamp:HH:mm:ss.fff}，跳过匹配逻辑",
                package.Barcode, package.Index, package.TriggerTimestamp);
        }
        else
        {
            Log.Information("收到包裹 {Barcode}(序号:{Index})，开始查找匹配的触发时间",
                package.Barcode, package.Index);

            // 查找匹配的触发时间并处理
            DateTime? matchedTriggerTime = null;

            // 使用锁确保线程安全
            lock (_triggerTimes)
            {
                var currentTime = DateTime.Now;
                var tempTriggerTimesList = _triggerTimes.ToList();

                // 记录当前时间和触发时间队列状态
                Log.Information("当前时间: {CurrentTime:HH:mm:ss.fff}, 触发时间队列中有 {Count} 个时间戳待匹配",
                    currentTime, tempTriggerTimesList.Count);

                if (tempTriggerTimesList.Count != 0)
                    Log.Information("触发时间队列内容: {Times}",
                        string.Join(", ", tempTriggerTimesList.Select(t =>
                            $"{t:HH:mm:ss.fff}[延迟:{(currentTime - t).TotalMilliseconds:F0}ms]")));

                // 清空原队列，准备重建
                _triggerTimes.Clear();

                // 查找匹配的触发时间并重建队列
                var found = false;
                var matchCount = 0; // 记录匹配的时间戳数量

                // 按时间顺序遍历触发时间
                foreach (var triggerTime in tempTriggerTimesList)
                {
                    // 计算时间差
                    var delay = (currentTime - triggerTime).TotalMilliseconds;

                    // 如果延迟已经超过上限，则将剩余时间戳全部重新入队
                    if (delay > Configuration.TriggerPhotoelectric.TimeRangeUpper)
                    {
                        Log.Debug("触发时间 {TriggerTime:HH:mm:ss.fff} 延迟 {Delay:F0}ms 超过上限 {Upper}ms，跳过",
                            triggerTime, delay, Configuration.TriggerPhotoelectric.TimeRangeUpper);
                        continue;
                    }

                    // 如果延迟小于下限，说明后面的时间戳更新，不可能匹配，提前结束查找
                    if (delay < Configuration.TriggerPhotoelectric.TimeRangeLower)
                    {
                        Log.Debug("触发时间 {TriggerTime:HH:mm:ss.fff} 延迟 {Delay:F0}ms 小于下限 {Lower}ms，重新入队",
                            triggerTime, delay, Configuration.TriggerPhotoelectric.TimeRangeLower);
                        // 将当前和剩余的时间戳重新入队
                        _triggerTimes.Enqueue(triggerTime);
                        continue;
                    }

                    // 时间戳在有效范围内
                    matchCount++;

                    if (!found)
                    {
                        matchedTriggerTime = triggerTime;
                        found = true;
                        Log.Information("包裹 {Barcode}(序号:{Index}) 匹配到触发时间 {TriggerTime:HH:mm:ss.fff}，延迟 {Delay:F0}ms",
                            package.Barcode, package.Index, triggerTime, delay);
                        package.ProcessingTime = delay;
                        continue;
                    }

                    // 将未匹配的时间戳重新入队
                    _triggerTimes.Enqueue(triggerTime);
                }

                // 检查是否有多个匹配的时间戳
                if (matchCount > 1)
                    Log.Warning("包裹 {Barcode}(序号:{Index}) 在时间范围内找到 {MatchCount} 个匹配的触发时间，" +
                                "建议调整触发时间范围设置（当前设置：{Lower}ms - {Upper}ms）",
                        package.Barcode, package.Index, matchCount,
                        Configuration.TriggerPhotoelectric.TimeRangeLower,
                        Configuration.TriggerPhotoelectric.TimeRangeUpper);

                // 记录重建后的队列状态
                var remainingTimes = _triggerTimes.ToList();
                if (remainingTimes.Count != 0)
                    Log.Debug("重建后的触发时间队列（{Count}个）: {Times}",
                        remainingTimes.Count,
                        string.Join(", ", remainingTimes.Select(static t => t.ToString("HH:mm:ss.fff"))));

                if (matchedTriggerTime.HasValue && !found)
                    Log.Warning("尝试从触发时间队列中移除时间戳 {TriggerTime}，但未找到", matchedTriggerTime.Value);
            }
             // 处理匹配结果
            if (matchedTriggerTime.HasValue)
            {
                // 设置包裹的触发时间戳
                package.SetTriggerTimestamp(matchedTriggerTime.Value);
            }
            else
            {
                 // 未找到匹配的触发时间
                Log.Warning("包裹 {Barcode}(序号:{Index}) 未找到匹配的触发时间", package.Barcode, package.Index);
            }
        }

        // 添加到待处理队列
        PendingSortPackages[package.Index] = package;
        Log.Information("包裹 {Barcode}(序号:{Index}) 已添加到待处理队列", package.Barcode, package.Index);

        // 创建超时定时器
        var timer = new Timer();
        timer.Elapsed += (_, _) => HandlePackageTimeout(package);

        // 默认超时时间 10 秒
        double timeoutInterval = 10000;
        string timeoutReason;

        // 获取对应分拣光电的配置
        var photoelectricName = GetPhotoelectricNameBySlot(package.ChuteNumber);
        if (photoelectricName != null)
        {
            var photoelectricConfig = GetPhotoelectricConfig(photoelectricName);
            // 设置超时时间为时间范围上限 + 500ms
            timeoutInterval = photoelectricConfig.TimeRangeUpper + 500;
            timeoutReason = $"光电配置上限 {photoelectricConfig.TimeRangeUpper}ms + 500ms";
        }
        else
        {
            Log.Warning("包裹 {Barcode}(序号:{Index}) 无法确定目标光电名称，使用默认超时 {DefaultTimeout}ms",
                package.Barcode, package.Index, timeoutInterval);
            timeoutReason = "无法确定目标光电，使用默认值";
        }

        timer.Interval = timeoutInterval;
        timer.AutoReset = false;
        PackageTimers[package.Index] = timer;
        timer.Start();

        Log.Debug("包裹 {Barcode}(序号:{Index}) 设置超时时间 {Timeout}ms ({Reason})",
            package.Barcode, package.Index, timer.Interval, timeoutReason);
    }

    public Dictionary<string, bool> GetAllDeviceConnectionStates()
    {
        return new Dictionary<string, bool>(_deviceConnectionStates);
    }

    public abstract Task<bool> UpdateConfigurationAsync(PendulumSortConfig configuration);

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void HandleConfigurationChanged(PendulumSortConfig newConfig)
    {
        var oldConfig = Configuration;
        Configuration = newConfig;

        // 检查触发光电连接参数是否变化
        if (TriggerClient != null &&
            (oldConfig.TriggerPhotoelectric.IpAddress != newConfig.TriggerPhotoelectric.IpAddress ||
             oldConfig.TriggerPhotoelectric.Port != newConfig.TriggerPhotoelectric.Port))
        {
            Log.Information("触发光电连接参数已变更，准备重新连接");
            _ = ReconnectAsync();
        }

        // 检查分拣光电连接参数是否变化（由子类实现具体逻辑）
        CheckSortingPhotoelectricChanges(oldConfig, newConfig);
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
        if (PackageTimers.TryRemove(package.Index, out var timer)) timer.Dispose();

        if (PendingSortPackages.TryRemove(package.Index, out _))
            Log.Warning("包裹 {Barcode}(序号:{Index}) 分拣超时，已从待处理队列移除",
                package.Barcode, package.Index);
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
            TimeoutCheckTimer.Dispose();
            TriggerClient?.Dispose();

            foreach (var timer in PackageTimers.Values) timer.Dispose();

            PackageTimers.Clear();
            CancellationTokenSource?.Dispose();

            // 取消配置变更订阅
            _settingsService.OnSettingsChanged<PendulumSortConfig>(null);
        }

        // 释放非托管资源

        _disposed = true;
    }

    /// <summary>
    ///     检查超时的包裹
    /// </summary>
    private void CheckTimeoutPackages(object? sender, ElapsedEventArgs e)
    {
        var now = DateTime.Now;
        var timeoutPackages = ProcessingPackages
            .Where(p => (now - p.Value.StartTime).TotalSeconds > 30) // 30秒超时
            .ToList();

        foreach (var package in timeoutPackages)
        {
            Log.Warning("包裹 {Barcode} 在 {PhotoelectricId} 处理超时", package.Key, package.Value.PhotoelectricId);
            ProcessingPackages.TryRemove(package.Key, out _);
        }
    }

    /// <summary>
    ///     触发设备连接状态变更事件
    /// </summary>
    private void RaiseDeviceConnectionStatusChanged(string deviceName, bool connected)
    {
        try
        {
            DeviceConnectionStatusChanged?.Invoke(this, (deviceName, connected));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "触发设备连接状态变更事件时发生错误");
        }
    }

    /// <summary>
    ///     更新设备连接状态
    /// </summary>
    protected void UpdateDeviceConnectionState(string deviceName, bool isConnected)
    {
        if (_deviceConnectionStates.TryGetValue(deviceName, out var currentState) && currentState == isConnected)
            return;

        _deviceConnectionStates[deviceName] = isConnected;
        RaiseDeviceConnectionStatusChanged(deviceName, isConnected);
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
    protected bool IsPackageProcessing(string barcode)
    {
        return ProcessingPackages.TryGetValue(barcode, out var status) && status.IsProcessing;
    }

    /// <summary>
    ///     标记包裹为处理中
    /// </summary>
    private void MarkPackageAsProcessing(string barcode, string photoelectricId)
    {
        ProcessingPackages[barcode] = new ProcessingStatus
        {
            StartTime = DateTime.Now,
            IsProcessing = true,
            PhotoelectricId = photoelectricId
        };
    }

    /// <summary>
    ///     处理触发光电信号
    /// </summary>
    protected void HandleTriggerPhotoelectric(string data)
    {
        // 记录触发时间
        var triggerTime = DateTime.Now;
        Log.Information("收到触发光电信号 {Signal}，记录触发时间 {TriggerTime:HH:mm:ss.fff}", data, triggerTime);

        lock (_triggerTimes)
        {
            _triggerTimes.Enqueue(triggerTime);
            Log.Information("触发时间已入队，当前队列长度: {Count}", _triggerTimes.Count);

            // 如果队列中的时间戳太多，移除最早的
            while (_triggerTimes.Count > 5) // <-- 修改此处，限制队列长度为 5
            {
                var removed = _triggerTimes.Dequeue();
                Log.Warning("触发时间队列超过5个，移除最早的时间戳: {RemovedTime:HH:mm:ss.fff}", removed);
            }

            // 打印当前队列中的所有触发时间
            var times = _triggerTimes.ToList();
            if (times.Count != 0)
                Log.Information("当前触发时间队列：{Times}",
                    string.Join(", ", times.Select(static t => t.ToString("HH:mm:ss.fff"))));
        }

        // 计算并记录触发延迟
        var delay = (DateTime.Now - triggerTime).TotalMilliseconds;
        _triggerDelays.Enqueue(delay);

        // 保持延迟队列在合理大小
        while (_triggerDelays.Count > 1000) _triggerDelays.TryDequeue(out _);
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
        // 处理可能的黏包情况，按行分割数据
        var lines = data.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            // 处理每一行数据
            if (line.Contains("OCCH1:1"))
            {
                Log.Debug("处理触发光电信号: {Signal}", line);
                HandleTriggerPhotoelectric(line);
            }

            if (line.Contains("OCCH2:1"))
            {
                Log.Debug("处理分拣光电信号: {Signal}", line);
                HandleSecondPhotoelectric(line);
            }
        }
    }

    /// <summary>
    ///     处理分拣信号并匹配包裹
    /// </summary>
    protected PackageInfo? MatchPackageForSorting(string photoelectricName)
    {
        Log.Information("收到分拣光电 {Name} 检测信号，开始匹配包裹", photoelectricName);

        // 获取当前时间
        var currentTime = DateTime.Now;

        // 获取所有待分拣包裹
        var packages = PendingSortPackages.Values
            .Where(p => !IsPackageProcessing(p.Barcode))
            .OrderBy(static p => p.Index)  // 首先按序号排序
            .ThenBy(static p => p.TriggerTimestamp)  // 然后按触发时间排序
            .ToList();

        if (packages.Count == 0)
        {
            Log.Warning("分拣光电 {Name} 触发，但没有待分拣的包裹", photoelectricName);
            return null;
        }

        // 打印当前待分拣队列状态
        Log.Information("当前待分拣队列中有 {Count} 个包裹:", packages.Count);
        foreach (var pkg in packages)
        {
            Log.Information("检查包裹 {Barcode}(序号:{Index}) 触发时间:{TriggerTime:HH:mm:ss.fff} 目标格口:{Slot}",
                pkg.Barcode, pkg.Index, pkg.TriggerTimestamp, pkg.ChuteNumber);

            // 检查包裹是否已超时
            if (PackageTimers.TryGetValue(pkg.Index, out var timer))
            {
                // 如果定时器已经停止，说明包裹已超时
                if (!timer.Enabled)
                {
                    Log.Information("包裹 {Barcode}(序号:{Index}) 已超时，从待处理队列中移除",
                        pkg.Barcode, pkg.Index);
                    PendingSortPackages.TryRemove(pkg.Index, out _);
                    timer.Dispose();
                    PackageTimers.TryRemove(pkg.Index, out _);
                    continue;
                }
            }

            // 检查包裹触发时间是否有效且是否应该由这个分拣光电处理
            if (pkg.TriggerTimestamp == default || !SlotBelongsToPhotoelectric(pkg.ChuteNumber, photoelectricName))
            {
                Log.Debug("包裹 {Barcode}(序号:{Index}) 不满足基本条件，跳过",
                    pkg.Barcode, pkg.Index);
                continue;
            }

            // 获取当前分拣光电的配置
            var photoelectricConfig = GetPhotoelectricConfig(photoelectricName);

            // 验证时间延迟
            var delay = (currentTime - pkg.TriggerTimestamp).TotalMilliseconds;
            var timeRangeLower = photoelectricConfig is SortPhotoelectric
                ? photoelectricConfig.TimeRangeLower
                : photoelectricConfig.SortingTimeRangeLower;
            var timeRangeUpper = photoelectricConfig is SortPhotoelectric
                ? photoelectricConfig.TimeRangeUpper
                : photoelectricConfig.SortingTimeRangeUpper;

            // 检查是否超时
            if (delay > timeRangeUpper + 500) // 使用与设置超时时间相同的逻辑
            {
                Log.Information("包裹 {Barcode}(序号:{Index}) 已超时(延迟:{Delay}ms > 上限:{Upper}ms + 500ms)，从待处理队列中移除",
                    pkg.Barcode, pkg.Index, delay, timeRangeUpper);

                // 从待处理队列中移除
                if (PendingSortPackages.TryRemove(pkg.Index, out _))
                {
                    // 如果定时器还存在，也一并清理
                    if (PackageTimers.TryRemove(pkg.Index, out var pkgTimer))
                    {
                        pkgTimer.Stop();
                        pkgTimer.Dispose();
                    }
                }
                continue;
            }

            if (delay < timeRangeLower || delay > timeRangeUpper)
            {
                Log.Information("包裹 {Barcode}(序号:{Index}) 目标格口匹配但时间延迟不符，延迟:{Delay:F2}ms，允许范围:{Lower}-{Upper}ms",
                    pkg.Barcode, pkg.Index, delay,
                    timeRangeLower,
                    timeRangeUpper);
                continue;
            }

            // 修改：不再从待处理队列中移除，只标记为处理中
            if (IsPackageProcessing(pkg.Barcode))
            {
                Log.Warning("包裹 {Barcode} 已被其他分拣光电处理", pkg.Barcode);
                continue;
            }

            // 标记包裹为处理中
            MarkPackageAsProcessing(pkg.Barcode, photoelectricName);
            return pkg;
        }

        Log.Debug("分拣光电 {Name} 没有找到符合条件的待分拣包裹", photoelectricName);
        return null;
    }

    /// <summary>
    ///     获取分拣光电配置
    /// </summary>
    protected virtual TriggerPhotoelectric GetPhotoelectricConfig(string photoelectricName)
    {
        return Configuration.SortingPhotoelectrics.First(p => p.Name == photoelectricName);
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
        try
        {
            // 获取用于执行分拣动作的客户端
            var client = GetSortingClient(photoelectricName);
            if (client == null || !client.IsConnected())
            {
                Log.Warning("分拣光电 {Name} 未连接，无法执行分拣动作", photoelectricName);
                ProcessingPackages.TryRemove(package.Barcode, out _);
                return;
            }

            // 获取光电配置
            var photoelectricConfig = GetPhotoelectricConfig(photoelectricName);
            var pendulumState = PendulumStates[photoelectricName];

            // 等待包裹到达最佳分拣位置
            await Task.Delay(photoelectricConfig.SortingDelay);
            Log.Debug("包裹 {Barcode} 等待 {Delay}ms 后开始执行分拣动作",
                package.Barcode, photoelectricConfig.SortingDelay);

            // 根据包裹目标格口决定摆动方向
            var targetSlot = package.ChuteNumber;
            var command = string.Empty;

            // 根据目标格口和当前状态决定命令
            if (ShouldSwingLeft(targetSlot))
            {
                if (pendulumState.GetCurrentState() == "Reset")
                {
                    command = PendulumCommands.Module2.SwingLeft;
                    pendulumState.SetSwing();
                }
                else if (pendulumState.LastSlot != targetSlot)
                {
                    // 先回正再摆动
                    var resetCommand = GetCommandBytes(PendulumCommands.Module2.ResetLeft);
                    if (!await SendCommandWithRetryAsync(client, resetCommand, photoelectricName))
                    {
                        Log.Error("发送左回正命令失败，无法执行分拣动作");
                        return;
                    }
                    await Task.Delay(photoelectricConfig.ResetDelay);

                    command = PendulumCommands.Module2.SwingLeft;
                    pendulumState.SetSwing();
                }
            }
            else if (ShouldSwingRight(targetSlot))
            {
                if (pendulumState.GetCurrentState() == "Reset")
                {
                    command = PendulumCommands.Module2.SwingRight;
                    pendulumState.SetSwing();
                }
                else if (pendulumState.LastSlot != targetSlot)
                {
                    // 先回正再摆动
                    var resetCommand = GetCommandBytes(PendulumCommands.Module2.ResetRight);
                    if (!await SendCommandWithRetryAsync(client, resetCommand, photoelectricName))
                    {
                        Log.Error("发送右回正命令失败，无法执行分拣动作");
                        return;
                    }
                    await Task.Delay(photoelectricConfig.ResetDelay);

                    command = PendulumCommands.Module2.SwingRight;
                    pendulumState.SetSwing();
                }
            }
            else if (pendulumState.GetCurrentState() == "Swing") // 其他格口，不需要摆动
            {
                // 需要回正
                command = pendulumState.LastSlot == 1
                    ? PendulumCommands.Module2.ResetLeft
                    : PendulumCommands.Module2.ResetRight;
                pendulumState.SetReset();
            }

            if (!string.IsNullOrEmpty(command))
            {
                var commandBytes = GetCommandBytes(command);
                if (!await SendCommandWithRetryAsync(client, commandBytes, photoelectricName))
                {
                    Log.Error("发送分拣命令失败");
                    return;
                }
                Log.Debug("已发送分拣命令到分拣光电 {Name}: {Command}", photoelectricName, command);
            }

            pendulumState.UpdateLastSlot(targetSlot);
            Log.Information("包裹 {Barcode} 分拣完成，目标格口: {TargetSlot}", package.Barcode, targetSlot);

            // 创建定时器，在指定延迟后回正
            if ((ShouldSwingLeft(targetSlot) || ShouldSwingRight(targetSlot)) &&
                pendulumState.GetCurrentState() == "Swing") // 只有在摆动状态才需要回正
            {
                var resetTimer = new Timer
                {
                    Interval = photoelectricConfig.ResetDelay,
                    AutoReset = false
                };

                resetTimer.Elapsed += async (_, _) =>
                {
                    resetTimer.Stop();
                    try
                    {
                        // 如果没有连接，直接返回
                        if (!client.IsConnected()) return;

                        // 修改：现在从PendingSortPackages中移除包裹，分拣动作完成
                        PendingSortPackages.TryRemove(package.Index, out _);
                        Log.Debug("包裹 {Barcode}(序号:{Index}) 分拣动作完成，从待处理队列中移除",
                            package.Barcode, package.Index);

                        // 延迟结束后，查找序号+1的包裹
                        var nextPackageIndex = package.Index + 1;
                        PendingSortPackages.TryGetValue(nextPackageIndex, out var nextPackage);

                        // 判断当前包裹和下一个包裹的摆动方向是否相同
                        var currentIsLeft = ShouldSwingLeft(targetSlot);
                        var skipReset = false;

                        if (nextPackage != null)
                        {
                            // 检查下一个包裹的目标格口是否与当前相同
                            skipReset = targetSlot == nextPackage.ChuteNumber;
                            ShouldSwingLeft(nextPackage.ChuteNumber);

                            Log.Debug(
                                "延迟结束后找到下一个序号包裹 {NextBarcode} (序号: {NextIndex})，当前格口: {CurrentSlot}，下一个格口: {NextSlot}{Action}",
                                nextPackage.Barcode, nextPackage.Index,
                                targetSlot,
                                nextPackage.ChuteNumber,
                                skipReset ? "，格口相同，跳过回正" : "，格口不同，需要回正");
                        }
                        else
                        {
                            Log.Debug("没有找到序号为 {NextIndex} 的待处理包裹，将执行回正", nextPackageIndex);
                        }

                        if (skipReset)
                        {
                            resetTimer.Dispose();
                            return;
                        }

                        // 执行回正
                        var resetCommand = currentIsLeft
                            ? GetCommandBytes(PendulumCommands.Module2.ResetLeft)  // 左摆用左回正
                            : GetCommandBytes(PendulumCommands.Module2.ResetRight); // 右摆用右回正

                        var commandStr = currentIsLeft
                            ? PendulumCommands.Module2.ResetLeft
                            : PendulumCommands.Module2.ResetRight;

                        if (await SendCommandWithRetryAsync(client, resetCommand, photoelectricName))
                        {
                            pendulumState.SetReset();
                            Log.Debug("已发送{Direction}回正命令到分拣光电 {Name}: {Command}，原因：{Reason}",
                                currentIsLeft ? "左" : "右",
                                photoelectricName,
                                commandStr,
                                nextPackage == null
                                    ? "无后续包裹"
                                    : $"下一个序号包裹目标格口不同 (当前:{targetSlot}, 下一个:{nextPackage.ChuteNumber})");
                        }
                        else
                        {
                            Log.Error("发送{Direction}回正命令({Command})失败，尝试强制回正",
                                currentIsLeft ? "左" : "右",
                                commandStr);
                            pendulumState.ForceReset();
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "发送回正命令到分拣光电 {Name} 失败", photoelectricName);
                        pendulumState.ForceReset();
                    }
                    finally
                    {
                        resetTimer.Dispose();
                    }
                };

                resetTimer.Start();
            }
            else
            {
                // 对于不需要回正的情况，直接从待处理队列中移除
                PendingSortPackages.TryRemove(package.Index, out _);
                Log.Debug("包裹 {Barcode}(序号:{Index}) 分拣动作完成且不需要回正，从待处理队列中移除",
                    package.Barcode, package.Index);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "执行分拣动作时发生错误");
            // 发生错误时，从待处理队列中移除
            PendingSortPackages.TryRemove(package.Index, out _);
        }
        finally
        {
            ProcessingPackages.TryRemove(package.Barcode, out _);
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
    protected virtual bool ShouldSwingLeft(int targetSlot)
    {
        // 奇数格口向左摆动
        return targetSlot % 2 == 1;
    }

    /// <summary>
    ///     判断是否需要向右摆动
    /// </summary>
    protected virtual bool ShouldSwingRight(int targetSlot)
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
        private DateTime _lastStateChangeTime = DateTime.Now;
        private string _lastCommand = string.Empty;
        public int LastSlot { get; private set; }

        public void SetSwing()
        {
            _isInReset = false;
            _lastStateChangeTime = DateTime.Now;
            _lastCommand = "Swing";
        }

        public void SetReset()
        {
            _isInReset = true;
            _lastStateChangeTime = DateTime.Now;
            _lastCommand = "Reset";
        }

        public void UpdateLastSlot(int slot)
        {
            LastSlot = slot;
        }

        public string GetCurrentState()
        {
            return _isInReset ? "Reset" : "Swing";
        }

        public DateTime GetLastStateChangeTime()
        {
            return _lastStateChangeTime;
        }

        public string GetLastCommand()
        {
            return _lastCommand;
        }

        public void ForceReset()
        {
            _isInReset = true;
            _lastStateChangeTime = DateTime.Now;
            _lastCommand = "ForceReset";
        }
    }

    /// <summary>
    ///     发送命令并重试
    /// </summary>
    private async Task<bool> SendCommandWithRetryAsync(TcpClientService client, byte[] command, string photoelectricName, int maxRetries = 3)
    {
        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                if (!client.IsConnected())
                {
                    Log.Warning("分拣光电 {Name} 未连接，尝试重连", photoelectricName);
                    await ReconnectAsync();
                    await Task.Delay(1000); // 等待重连完成
                    continue;
                }

                client.Send(command);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "发送命令到分拣光电 {Name} 失败，第 {Retry} 次重试", photoelectricName, i + 1);
                if (i < maxRetries - 1)
                {
                    await Task.Delay(1000); // 等待1秒后重试
                }
            }
        }
        return false;
    }
}