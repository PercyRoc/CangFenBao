using System.Collections.Concurrent;
using System.Text;
using System.Timers;
using Common.Models.Package;
using Common.Models.Settings.Sort.PendulumSort;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.TCP;
using Serilog;
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
                    if (delay > _settingsService.LoadSettings<PendulumSortConfig>().TriggerPhotoelectric.TimeRangeUpper)
                    {
                        Log.Debug("触发时间 {TriggerTime:HH:mm:ss.fff} 延迟 {Delay:F0}ms 超过上限 {Upper}ms，跳过",
                            triggerTime, delay,
                            _settingsService.LoadSettings<PendulumSortConfig>().TriggerPhotoelectric.TimeRangeUpper);
                        continue;
                    }

                    // 如果延迟小于下限，说明后面的时间戳更新，不可能匹配，提前结束查找
                    if (delay < _settingsService.LoadSettings<PendulumSortConfig>().TriggerPhotoelectric.TimeRangeLower)
                    {
                        Log.Debug("触发时间 {TriggerTime:HH:mm:ss.fff} 延迟 {Delay:F0}ms 小于下限 {Lower}ms，重新入队",
                            triggerTime, delay,
                            _settingsService.LoadSettings<PendulumSortConfig>().TriggerPhotoelectric.TimeRangeLower);
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
                        _settingsService.LoadSettings<PendulumSortConfig>().TriggerPhotoelectric.TimeRangeLower,
                        _settingsService.LoadSettings<PendulumSortConfig>().TriggerPhotoelectric.TimeRangeUpper);

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
    private bool IsPackageProcessing(string barcode)
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
    protected abstract Task HandleSecondPhotoelectricAsync(string data);

    /// <summary>
    ///     处理光电信号
    /// </summary>
    protected async Task HandlePhotoelectricSignalAsync(string data)
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

            if (!line.Contains("OCCH2:1")) continue;
            Log.Debug("处理分拣光电信号: {Signal}", line);
            await HandleSecondPhotoelectricAsync(line);
        }
    }

    /// <summary>
    ///     处理分拣信号并匹配包裹
    /// </summary>
    protected async Task<PackageInfo?> MatchPackageForSorting(string photoelectricName)
    {
        Log.Information("收到分拣光电 {Name} 检测信号，开始匹配包裹", photoelectricName);

        // 获取当前时间
        var currentTime = DateTime.Now;

        // 获取所有待分拣包裹
        var packages = PendingSortPackages.Values
            .Where(p => !IsPackageProcessing(p.Barcode))
            .OrderBy(static p => p.Index) // 首先按序号排序
            .ThenBy(static p => p.TriggerTimestamp) // 然后按触发时间排序
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
            var timeDiff = (currentTime - pkg.TriggerTimestamp).TotalMilliseconds;
            Log.Information("检查包裹 {Barcode}(序号:{Index}) 触发时间:{TriggerTime:HH:mm:ss.fff} 目标格口:{Slot}，与当前分拣信号时间差:{TimeDiff}ms",
                pkg.Barcode, pkg.Index, pkg.TriggerTimestamp, pkg.ChuteNumber, timeDiff);

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

        // 如果循环完成没有找到匹配的包裹，则执行以下逻辑
        var pendulumState = PendulumStates.TryGetValue(photoelectricName, out var state) ? state : null;
        if (pendulumState != null && pendulumState.GetCurrentState() == "Swing")
        {
            Log.Information("分拣光电 {Name} 未找到匹配包裹，且摆轮当前为 {PendulumState} 状态。将尝试发送两次回正命令。", photoelectricName, pendulumState.GetCurrentState());
            var client = GetSortingClient(photoelectricName);
            if (client != null && client.IsConnected())
            {
                string resetCommandString;
                // 根据LastSlot决定回正方向，如果LastSlot为0，则默认为右回正
                if (ShouldSwingLeft(pendulumState.LastSlot) && pendulumState.LastSlot != 0)
                {
                    resetCommandString = PendulumCommands.Module2.ResetLeft;
                }
                else
                {
                    resetCommandString = PendulumCommands.Module2.ResetRight;
                }
                
                var commandBytes = GetCommandBytes(resetCommandString);
                Log.Information("分拣光电 {Name} 准备发送回正命令 ({Command}) 两次。基于 LastSlot: {LastSlot}", photoelectricName, resetCommandString, pendulumState.LastSlot);

                await SendCommandWithRetryAsync(client, commandBytes, photoelectricName, resetCommandString); // 第一次发送
                await SendCommandWithRetryAsync(client, commandBytes, photoelectricName, resetCommandString); // 第二次发送
                
                Log.Information("分拣光电 {Name} 已发送回正命令 ({Command}) 两次。", photoelectricName, resetCommandString);
                pendulumState.SetReset(); // 更新摆轮状态为已回正
            }
            else
            {
                Log.Warning("分拣光电 {Name} 客户端未连接或未找到，无法发送回正命令。摆轮状态仍为 {PendulumState}，LastSlot: {LastSlot}", photoelectricName, pendulumState.GetCurrentState(), pendulumState.LastSlot);
                // 可选：如果客户端不存在，强制重置逻辑状态以避免卡住
                // pendulumState.ForceReset();
            }
        }

        Log.Debug("分拣光电 {Name} 没有找到符合条件的待分拣包裹", photoelectricName);
        return null;
    }

    /// <summary>
    ///     获取分拣光电配置
    /// </summary>
    protected virtual TriggerPhotoelectric GetPhotoelectricConfig(string photoelectricName)
    {
        return _settingsService.LoadSettings<PendulumSortConfig>().SortingPhotoelectrics
            .First(p => p.Name == photoelectricName);
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
        var pendulumState = PendulumStates[photoelectricName]; 

        pendulumState.CancelPendingReset();

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
            // var pendulumState = PendulumStates[photoelectricName]; // Already got this

            // 等待包裹到达最佳分拣位置
            await Task.Delay(photoelectricConfig.SortingDelay);
            Log.Debug("包裹 {Barcode} 等待 {Delay}ms 后开始执行分拣动作",
                package.Barcode, photoelectricConfig.SortingDelay);

            // 根据包裹目标格口决定摆动方向
            var targetSlot = package.ChuteNumber;
            var command = string.Empty;
            var commandDescription = string.Empty; 

            // 新的命令决策逻辑
            if (pendulumState.GetCurrentState() == "Swing")
            {
                bool needsReset = false;
                string resetCmd = "";
                string resetCmdDesc = "";

                if (ShouldSwingLeft(targetSlot)) // 新目标是左
                {
                    if (pendulumState.LastSlot != targetSlot) // 如果不是已经为这个目标左摆
                    {
                        needsReset = true; 
                        // 根据上次摆动方向选择回正命令
                        if (ShouldSwingRight(pendulumState.LastSlot)) // 如果上次是右摆
                        {
                            resetCmd = PendulumCommands.Module2.ResetRight;
                            resetCmdDesc = "回正(从右为左摆准备)";
                        }
                        else // 上次是左摆 (到不同格口) 或初始状态
                        {
                            resetCmd = PendulumCommands.Module2.ResetLeft;
                            resetCmdDesc = "回正(从左为左摆准备)";
                        }
                    }
                    // 如果 pendulumState.LastSlot == targetSlot 并且是左摆目标，则不需要做任何事，command将为空
                }
                else if (ShouldSwingRight(targetSlot)) // 新目标是右
                {
                    if (pendulumState.LastSlot != targetSlot) // 如果不是已经为这个目标右摆
                    {
                        needsReset = true; 
                        // 根据上次摆动方向选择回正命令
                        if (ShouldSwingLeft(pendulumState.LastSlot)) // 如果上次是左摆
                        {
                            resetCmd = PendulumCommands.Module2.ResetLeft;
                            resetCmdDesc = "回正(从左为右摆准备)";
                        }
                        else // 上次是右摆 (到不同格口) 或初始状态
                        {
                            resetCmd = PendulumCommands.Module2.ResetRight;
                            resetCmdDesc = "回正(从右为右摆准备)";
                        }
                    }
                    // 如果 pendulumState.LastSlot == targetSlot 并且是右摆目标，则不需要做任何事，command将为空
                }
                else // 新目标是中间 (不需要特定摆动)
                {
                    // 如果当前是摆动状态，则必须回正
                    needsReset = true; 
                    if (ShouldSwingLeft(pendulumState.LastSlot) || pendulumState.LastSlot == 0) // 如果上次是左摆或初始状态
                    {
                        resetCmd = PendulumCommands.Module2.ResetLeft;
                        resetCmdDesc = "回正(到中间)";
                    }
                    else // 上次是右摆
                    {
                        resetCmd = PendulumCommands.Module2.ResetRight;
                        resetCmdDesc = "回正(到中间)";
                    }
                }

                if (needsReset && !string.IsNullOrEmpty(resetCmd))
                {
                    var resetCommandBytes = GetCommandBytes(resetCmd);
                    if (!await SendCommandWithRetryAsync(client, resetCommandBytes, photoelectricName, resetCmdDesc))
                    {
                        Log.Error("发送预备回正命令 ('{Desc}') 失败. 包裹: {Barcode}, 光电: {Photoelectric}", resetCmdDesc, package.Barcode, photoelectricName);
                        ProcessingPackages.TryRemove(package.Barcode, out _);
                        return;
                    }
                    pendulumState.SetReset(); // 逻辑状态设置为 Reset
                    Log.Debug("预备回正后，摆轮 {Name} 状态设置为 Reset", photoelectricName);
                    await Task.Delay(20); // 等待物理回正完成 - 固定20ms延迟
                }
            }

            // 此时，如果需要回正，则 pendulumState.GetCurrentState() 应该是 "Reset"
            // 现在根据当前状态（可能是刚被置为Reset，或者一开始就是Reset，或者是不需要改变的Swing）决定最终的摆动命令
            if (pendulumState.GetCurrentState() == "Reset")
            {
                if (ShouldSwingLeft(targetSlot))
                {
                    command = PendulumCommands.Module2.SwingLeft;
                    commandDescription = "摆动到左侧";
                    pendulumState.SetSwing();
                }
                else if (ShouldSwingRight(targetSlot))
                {
                    command = PendulumCommands.Module2.SwingRight;
                    commandDescription = "摆动到右侧";
                    pendulumState.SetSwing();
                }
                // 如果目标是中间且当前是Reset，command 保持为空，不需要动作
            }
            // 如果 pendulumState.GetCurrentState() 仍是 "Swing"，那意味着它已经是正确的方向和目标格口，command将为空

            if (!string.IsNullOrEmpty(command))
            {
                var commandBytes = GetCommandBytes(command);
                if (!await SendCommandWithRetryAsync(client, commandBytes, photoelectricName, commandDescription))
                {
                    Log.Error("发送分拣命令 '{CommandDesc}' ({CommandText}) 失败. 包裹: {Barcode}, 光电: {Photoelectric}", commandDescription, command, package.Barcode, photoelectricName);
                    ProcessingPackages.TryRemove(package.Barcode, out _); 
                    return;
                }
            }

            pendulumState.UpdateLastSlot(targetSlot);
            Log.Information("包裹 {Barcode} 分拣完成，目标格口: {TargetSlot}", package.Barcode, targetSlot);
            
            PendingSortPackages.TryRemove(package.Index, out _); // 包裹已分拣或命令已发出，从待处理移除
            Log.Debug("包裹 {Barcode}(序号:{Index}) 已执行分拣动作或命令，从待处理队列中移除", package.Barcode, package.Index);


            if ((ShouldSwingLeft(targetSlot) || ShouldSwingRight(targetSlot)) &&
                pendulumState.GetCurrentState() == "Swing")
            {
                Func<Task<bool>> resetAction = async () => {
                    // client can be null if ExecuteSortingAction was called for a different package and this timer fires later
                    // or if the client was disposed. We need to get a fresh client instance or ensure it's still valid.
                    var currentClient = GetSortingClient(photoelectricName);
                    if (currentClient == null || !currentClient.IsConnected()) 
                    {
                        Log.Warning("延迟回正: 客户端 {Name} 未连接或为null，无法回正。", photoelectricName);
                        return false; 
                    }

                    var currentTargetIsLeftThatCausedSwing = ShouldSwingLeft(pendulumState.LastSlot); // Use LastSlot to determine the direction of the swing that scheduled this reset
                    var resetCmdBytes = currentTargetIsLeftThatCausedSwing 
                        ? GetCommandBytes(PendulumCommands.Module2.ResetLeft)
                        : GetCommandBytes(PendulumCommands.Module2.ResetRight);
                    var resetCmdDesc = currentTargetIsLeftThatCausedSwing ? "延迟回正(左)" : "延迟回正(右)";
                    
                    return await SendCommandWithRetryAsync(currentClient, resetCmdBytes, photoelectricName, resetCmdDesc);
                };

                pendulumState.ScheduleDelayedReset(photoelectricConfig.ResetDelay, resetAction);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "执行分拣动作时发生错误. 包裹: {Barcode}, 光电: {Photoelectric}", package.Barcode, photoelectricName);
            ProcessingPackages.TryRemove(package.Barcode, out _);
            PendingSortPackages.TryRemove(package.Index, out _); // Ensure removal on error too
        }
        finally
        {
            // This was moved up: ProcessingPackages.TryRemove(package.Barcode, out _);
            // However, if an exception occurs before this line in the try block, it might not be removed.
            // Ensure it's removed if an error occurred and it's still there.
            if (ProcessingPackages.ContainsKey(package.Barcode))
            {
                ProcessingPackages.TryRemove(package.Barcode, out _);
                Log.Debug("确保包裹 {Barcode} 在ExecuteSortingAction结束时从ProcessingPackages中移除 (可能由于异常)", package.Barcode);
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
    protected class PendulumState : IDisposable
    {
        private bool _isInReset = true;
        public int LastSlot { get; private set; }
        private Timer? _pendingResetTimer; // Timer for delayed reset
        private readonly string _photoelectricName; 
        private Func<Task<bool>>? _resetActionAsync;

        public PendulumState(string photoelectricName)
        {
            _photoelectricName = photoelectricName;
        }

        public void SetSwing()
        {
            CancelPendingReset(); 
            _isInReset = false;
        }

        public void SetReset()
        {
            CancelPendingReset(); 
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
            CancelPendingReset();
            _isInReset = true;
            Log.Information("Pendulum state for {PhotoelectricName} forced to Reset.", _photoelectricName);
        }

        public void CancelPendingReset()
        {
            if (_pendingResetTimer != null)
            {
                _pendingResetTimer.Stop();
                _pendingResetTimer.Elapsed -= OnPendingResetTimerElapsed; 
                _pendingResetTimer.Dispose();
                _pendingResetTimer = null;
                Log.Debug("Pending reset timer cancelled for {PhotoelectricName}.", _photoelectricName);
            }
        }

        public void ScheduleDelayedReset(double interval, Func<Task<bool>> resetActionAsync)
        {
            CancelPendingReset(); 

            _resetActionAsync = resetActionAsync ?? throw new ArgumentNullException(nameof(resetActionAsync));

            _pendingResetTimer = new Timer(interval)
            {
                AutoReset = false
            };
            _pendingResetTimer.Elapsed += OnPendingResetTimerElapsed;
            _pendingResetTimer.Start();
            Log.Debug("Scheduled delayed reset for {PhotoelectricName} in {Interval}ms.", _photoelectricName, interval);
        }

        private async void OnPendingResetTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            var timerThatElapsed = _pendingResetTimer; // Capture the instance

            Log.Information("Pending reset timer elapsed for {PhotoelectricName}. Current state before action: {State}, LastSlot: {LastSlot}. Executing reset action.", 
                _photoelectricName, GetCurrentState(), LastSlot);
            
            // It's crucial to nullify and dispose the timer *before* any await that might switch context,
            // or at least ensure this specific instance won't be reused or double-disposed if CancelPendingReset is called elsewhere.
            // Simplest is to unsubscribe and dispose, then nullify the class member.
            if (timerThatElapsed != null)
            {
                 timerThatElapsed.Stop(); // Ensure it's stopped
                 timerThatElapsed.Elapsed -= OnPendingResetTimerElapsed; // Unsubscribe
                 timerThatElapsed.Dispose();
                 if (_pendingResetTimer == timerThatElapsed) // Only nullify if it's still the current timer
                 {
                    _pendingResetTimer = null;
                 }
            }

            if (_isInReset && _resetActionAsync == null) // If already reset and no action (e.g. by another operation or a quick succession), do nothing
            {
                Log.Information("Reset timer for {PhotoelectricName} elapsed, but pendulum is already in Reset state or action is null. No action taken.", _photoelectricName);
                return;
            }
            
            if (_resetActionAsync == null)
            {
                Log.Warning("Reset timer for {PhotoelectricName} elapsed, but no reset action was defined.", _photoelectricName);
                // Force reset the logical state if no action can be performed
                _isInReset = true; 
                return;
            }

            try
            {
                var success = await _resetActionAsync();
                if (success)
                {
                    _isInReset = true; 
                    Log.Information("Delayed reset action successfully executed for {PhotoelectricName}. State is now Reset.", _photoelectricName);
                }
                else
                {
                    Log.Warning("Delayed reset action failed for {PhotoelectricName}. State remains Swing (or previous state). LastSlot: {LastSlot}", _photoelectricName, LastSlot);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Exception during delayed reset action for {PhotoelectricName}. State remains Swing (or previous state). LastSlot: {LastSlot}", _photoelectricName, LastSlot);
                // ForceReset(); // Consider forcing reset on exception
            }
            finally
            {
                _resetActionAsync = null; // Clear the action after execution
            }
        }

        public void Dispose()
        {
            CancelPendingReset();
        }
    }

    /// <summary>
    ///     发送命令并重试
    /// </summary>
    private async Task<bool> SendCommandWithRetryAsync(TcpClientService client, byte[] command, string photoelectricName, string commandDescription, int maxRetries = 3)
    {
        var commandText = Encoding.ASCII.GetString(command).TrimEnd(); // 用于日志记录，移除 \r\n
        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                if (!client.IsConnected())
                {
                    Log.Warning("分拣光电 {Name} 未连接，尝试重连 (发送 '{CommandDesc}' ({CommandText}) 期间)", photoelectricName, commandDescription, commandText);
                    await ReconnectAsync(); // 应该只尝试重连一次，或者有更复杂的重连策略
                    await Task.Delay(1000); // 等待重连完成
                    // 如果重连后仍未连接，则此次尝试失败，进入下一次重试或最终失败
                    if(!client.IsConnected()) continue;
                }

                client.Send(command);
                Log.Information("已发送命令 '{CommandDesc}' ({CommandText}) 到光电 {PhotoelectricName}", commandDescription, commandText, photoelectricName);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "发送命令 '{CommandDesc}' ({CommandText}) 到分拣光电 {Name} 失败，第 {Retry} 次重试", commandDescription, commandText, photoelectricName, i + 1);
                if (i < maxRetries - 1)
                {
                    await Task.Delay(1000); // 等待1秒后重试
                }
            }
        }
        Log.Error("发送命令 '{CommandDesc}' ({CommandText}) 到分拣光电 {Name} 最终失败 (尝试 {MaxRetries} 次)", commandDescription, commandText, photoelectricName, maxRetries);
        return false;
    }
}