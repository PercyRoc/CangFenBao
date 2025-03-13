using System.Collections.Concurrent;
using System.Text;
using System.Timers;
using Common.Models.Package;
using Common.Services.Settings;
using Serilog;
using SortingServices.Common;
using SortingServices.Pendulum.Models;
using Timer = System.Timers.Timer;

namespace SortingServices.Pendulum;

/// <summary>
/// 摆轮分拣服务基类，提供单光电单摆轮和多光电多摆轮共同的功能
/// </summary>
public abstract class BasePendulumSortService : IPendulumSortService
{
    private readonly ConcurrentDictionary<string, bool> _deviceConnectionStates = new();
    protected readonly ConcurrentDictionary<int, Timer> PackageTimers = new();
    protected readonly ConcurrentDictionary<int, PackageInfo> PendingSortPackages = new();
    protected readonly ConcurrentDictionary<string, PendulumState> PendulumStates = new();
    protected readonly ConcurrentDictionary<string, ProcessingStatus> ProcessingPackages = new();
    protected readonly Timer TimeoutCheckTimer;
    private readonly ConcurrentQueue<double> _triggerDelays = new();
    private readonly Queue<DateTime> _triggerTimes = new();
    protected CancellationTokenSource? CancellationTokenSource;
    protected PendulumSortConfig Configuration = new();
    private bool _disposed;
    protected bool IsRunningFlag;
    protected TcpClientService? TriggerClient;
    protected readonly ConcurrentDictionary<string, PackageInfo> MatchedPackages = new();
    private readonly ISettingsService _settingsService;

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
    /// 重新连接设备
    /// </summary>
    protected abstract Task ReconnectAsync();

    /// <summary>
    /// 检查分拣光电配置变更，由子类实现具体逻辑
    /// </summary>
    protected virtual void CheckSortingPhotoelectricChanges(PendulumSortConfig oldConfig, PendulumSortConfig newConfig)
    {
        // 基类不实现具体逻辑
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
        
        Log.Information("收到包裹 {Barcode}(序号:{Index})，开始查找匹配的触发时间", 
            package.Barcode, package.Index);

        // 查找匹配的触发时间并处理
        DateTime? matchedTriggerTime = null;
        
        // 使用锁确保线程安全
        lock (_triggerTimes)
        {
            var currentTime = DateTime.Now;
            var tempTriggerTimesList = _triggerTimes.ToList();
            
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
                    // 当前时间戳太早，直接跳过
                    continue;
                }
                
                // 如果延迟小于下限，说明后面的时间戳更新，不可能匹配，提前结束查找
                if (delay < Configuration.TriggerPhotoelectric.TimeRangeLower)
                {
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
                    Log.Information("包裹 {Barcode}(序号:{Index}) 匹配到触发时间 {TriggerTime:HH:mm:ss.fff}，延迟 {Delay}ms", 
                        package.Barcode, package.Index, triggerTime, delay);
                    continue;
                }
                
                // 将未匹配的时间戳重新入队
                _triggerTimes.Enqueue(triggerTime);
            }
            
            // 检查是否有多个匹配的时间戳
            if (matchCount > 1)
            {
                Log.Warning("包裹 {Barcode}(序号:{Index}) 在时间范围内找到 {MatchCount} 个匹配的触发时间，" +
                          "建议调整触发时间范围设置（当前设置：{Lower}ms - {Upper}ms）", 
                    package.Barcode, package.Index, matchCount,
                    Configuration.TriggerPhotoelectric.TimeRangeLower,
                    Configuration.TriggerPhotoelectric.TimeRangeUpper);
            }
            package.SetError("触发时间范围设置需要调整");
            if (matchedTriggerTime.HasValue && !found)
            {
                Log.Warning("尝试从触发时间队列中移除时间戳 {TriggerTime}，但未找到", matchedTriggerTime.Value);
            }
        }
        
        // 处理匹配结果
        if (matchedTriggerTime.HasValue)
        {
            // 设置包裹的触发时间戳
            package.TriggerTimestamp = matchedTriggerTime.Value;
            
            // 添加到待处理队列
            PendingSortPackages[package.Index] = package;
            Log.Information("包裹 {Barcode}(序号:{Index}) 已添加到待处理队列", package.Barcode, package.Index);

            // 创建超时定时器
            var timer = new Timer();
            timer.Elapsed += (_, _) => HandlePackageTimeout(package);
            
            // 获取对应分拣光电的配置
            var photoelectricName = GetPhotoelectricNameBySlot(package.ChuteName);
            if (photoelectricName != null)
            {
                var photoelectricConfig = GetPhotoelectricConfig(photoelectricName);
                // 设置超时时间为时间范围上限 + 500ms
                timer.Interval = photoelectricConfig.SortingTimeRangeUpper + 500;
                timer.AutoReset = false;
                PackageTimers[package.Index] = timer;
                timer.Start();
                
                Log.Debug("包裹 {Barcode}(序号:{Index}) 设置超时时间 {Timeout}ms", 
                    package.Barcode, package.Index, timer.Interval);
            }
        }
        else
        {
            // 未找到匹配的触发时间，仍然添加到待处理队列
            PendingSortPackages[package.Index] = package;
            Log.Warning("包裹 {Barcode}(序号:{Index}) 未找到匹配的触发时间，仍添加到待处理队列", 
                package.Barcode, package.Index);
        }
    }

    /// <summary>
    /// 处理包裹超时
    /// </summary>
    private void HandlePackageTimeout(PackageInfo package)
    {
        if (PackageTimers.TryRemove(package.Index, out var timer))
        {
            timer.Dispose();
        }

        if (PendingSortPackages.TryRemove(package.Index, out _))
        {
            Log.Warning("包裹 {Barcode}(序号:{Index}) 分拣超时，已从待处理队列移除", 
                package.Barcode, package.Index);
        }
    }

    /// <summary>
    /// 根据格口获取对应的分拣光电名称
    /// </summary>
    protected virtual string? GetPhotoelectricNameBySlot(int slot)
    {
        // 基类默认返回null，由子类实现具体逻辑
        return null;
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

    private void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            // 释放托管资源
            StopAsync().Wait();
            TimeoutCheckTimer.Dispose();
            TriggerClient?.Dispose();

            foreach (var timer in PackageTimers.Values)
            {
                timer.Dispose();
            }

            PackageTimers.Clear();
            CancellationTokenSource?.Dispose();

            // 取消配置变更订阅
            _settingsService.OnSettingsChanged<PendulumSortConfig>(null);
        }

        // 释放非托管资源

        _disposed = true;
    }

    /// <summary>
    /// 检查超时的包裹
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
    /// 触发设备连接状态变更事件
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
    /// 更新设备连接状态
    /// </summary>
    protected void UpdateDeviceConnectionState(string deviceName, bool isConnected)
    {
        if (_deviceConnectionStates.TryGetValue(deviceName, out var currentState) && currentState == isConnected)
            return;

        _deviceConnectionStates[deviceName] = isConnected;
        RaiseDeviceConnectionStatusChanged(deviceName, isConnected);
    }

    /// <summary>
    /// 包裹处理状态类
    /// </summary>
    protected class ProcessingStatus
    {
        public DateTime StartTime { get; init; }
        public bool IsProcessing { get; init; }
        public string PhotoelectricId { get; init; } = string.Empty;
    }

    /// <summary>
    /// 摆轮命令结构体
    /// </summary>
    protected struct PendulumCommands
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
    /// 摆轮状态类
    /// </summary>
    protected class PendulumState
    {
        private bool IsInReset { get; set; } = true;
        public int LastSlot { get; private set; }

        public void SetSwing()
        {
            IsInReset = false;
        }

        public void SetReset()
        {
            IsInReset = true;
        }

        public void UpdateLastSlot(int slot)
        {
            LastSlot = slot;
        }

        public string GetCurrentState()
        {
            return IsInReset ? "Reset" : "Swing";
        }
    }

    /// <summary>
    /// 将命令字符串转换为字节数组
    /// </summary>
    protected static byte[] GetCommandBytes(string command)
    {
        // 添加回车换行符
        command += "\r\n";
        return Encoding.ASCII.GetBytes(command);
    }

    /// <summary>
    /// 检查包裹是否正在处理
    /// </summary>
    protected bool IsPackageProcessing(string barcode)
    {
        return ProcessingPackages.TryGetValue(barcode, out var status) && status.IsProcessing;
    }

    /// <summary>
    /// 标记包裹为处理中
    /// </summary>
    protected void MarkPackageAsProcessing(string barcode, string photoelectricId)
    {
        ProcessingPackages[barcode] = new ProcessingStatus
        {
            StartTime = DateTime.Now,
            IsProcessing = true,
            PhotoelectricId = photoelectricId
        };
    }

    /// <summary>
    /// 处理触发光电信号
    /// </summary>
    private void HandleTriggerPhotoelectric(string data)
    {
        if (data != "+OCCH1:1") return;
        
        // 记录触发时间
        var triggerTime = DateTime.Now;
        Log.Debug("收到触发光电信号，记录触发时间 {TriggerTime:HH:mm:ss.fff}", triggerTime);
        
        lock (_triggerTimes)
        {
            _triggerTimes.Enqueue(triggerTime);
            
            // 如果队列中的时间戳太多，移除最早的
            while (_triggerTimes.Count > 100)
            {
                _triggerTimes.Dequeue();
                Log.Warning("触发时间队列已满，移除最早的时间戳");
            }
        }
        
        // 计算并记录触发延迟
        var delay = (triggerTime - DateTime.Now).TotalMilliseconds;
        _triggerDelays.Enqueue(delay);
        
        // 保持延迟队列在合理大小
        while (_triggerDelays.Count > 1000)
        {
            _triggerDelays.TryDequeue(out _);
        }
    }

    /// <summary>
    /// 处理第二光电信号，由子类实现具体逻辑
    /// </summary>
    protected abstract void HandleSecondPhotoelectric(string data);
    
    /// <summary>
    /// 处理光电信号
    /// </summary>
    protected void HandlePhotoelectricSignal(string data)
    {
        if (data == "+OCCH1:1")
        {
            HandleTriggerPhotoelectric(data);
        }
        else if (data == "+OCCH2:1")
        {
            HandleSecondPhotoelectric(data);
        }
    }

    /// <summary>
    /// 处理分拣信号并匹配包裹
    /// </summary>
    protected PackageInfo? MatchPackageForSorting(string photoelectricName)
    {
        Log.Information("收到分拣光电 {Name} 检测信号，开始匹配包裹", photoelectricName);

        // 获取当前时间
        var currentTime = DateTime.Now;
            
        // 获取所有待分拣包裹
        var packages = PendingSortPackages.Values
            .Where(p => !IsPackageProcessing(p.Barcode))
            .OrderBy(p => p.TriggerTimestamp)
            .ThenBy(p => p.Index)
            .ToList();

        if (packages.Count == 0)
        {
            Log.Warning("分拣光电 {Name} 触发，但没有待分拣的包裹", photoelectricName);
            return null;
        }

        // 查找第一个符合条件的包裹
        var package = packages.FirstOrDefault(p => 
            // 检查包裹触发时间是否有效
            p.TriggerTimestamp != default && 
            // 检查包裹是否应该由这个分拣光电处理
            SlotBelongsToPhotoelectric(p.ChuteName, photoelectricName));

        if (package == null)
        {
            Log.Debug("分拣光电 {Name} 没有找到符合条件的待分拣包裹", photoelectricName);
            return null;
        }

        // 获取当前分拣光电的配置
        var photoelectricConfig = GetPhotoelectricConfig(photoelectricName);
            
        // 验证时间延迟
        var delay = (currentTime - package.TriggerTimestamp).TotalMilliseconds;
        if (delay < photoelectricConfig.SortingTimeRangeLower ||
            delay > photoelectricConfig.SortingTimeRangeUpper)
        {
            Log.Debug("包裹 {Barcode}(序号:{Index}) 分拣时间延迟验证失败，延迟:{Delay}ms，允许范围:{Lower}-{Upper}ms",
                package.Barcode, package.Index, delay,
                photoelectricConfig.SortingTimeRangeLower,
                photoelectricConfig.SortingTimeRangeUpper);
            return null;
        }

        // 从待处理队列中移除
        if (!PendingSortPackages.TryRemove(package.Index, out _))
        {
            Log.Warning("包裹 {Barcode} 已被其他分拣光电处理", package.Barcode);
            return null;
        }

        // 标记包裹为处理中
        MarkPackageAsProcessing(package.Barcode, photoelectricName);

        Log.Information("分拣光电 {Name} 匹配到包裹 {Barcode}，等待执行分拣动作", 
            photoelectricName, package.Barcode);

        return package;
    }

    /// <summary>
    /// 获取分拣光电配置
    /// </summary>
    protected virtual TriggerPhotoelectric GetPhotoelectricConfig(string photoelectricName)
    {
        return Configuration.SortingPhotoelectrics.First(p => p.Name == photoelectricName);
    }

    /// <summary>
    /// 判断格口是否属于指定的分拣光电
    /// </summary>
    protected virtual bool SlotBelongsToPhotoelectric(int targetSlot, string photoelectricName)
    {
        return true; // 基类默认返回true，由子类实现具体逻辑
    }

    /// <summary>
    /// 执行分拣动作
    /// </summary>
    protected virtual async Task ExecuteSortingAction(PackageInfo package, string photoelectricName)
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

            // 等待包裹到达最佳分拣位置
            await Task.Delay(photoelectricConfig.SortingDelay);
            Log.Debug("包裹 {Barcode} 等待 {Delay}ms 后开始执行分拣动作", 
                package.Barcode, photoelectricConfig.SortingDelay);

            var pendulumState = PendulumStates[photoelectricName];
            var currentState = pendulumState.GetCurrentState();

            // 根据包裹目标格口决定摆动方向
            var targetSlot = package.ChuteName;
            var command = string.Empty;

            // 根据目标格口和当前状态决定命令
            if (ShouldSwingLeft(targetSlot))
            {
                if (currentState == "Reset")
                {
                    command = PendulumCommands.Module2.SwingLeft;
                    pendulumState.SetSwing();
                }
                else if (pendulumState.LastSlot != targetSlot)
                {
                    // 先回正再摆动
                    var resetCommand = GetCommandBytes(PendulumCommands.Module2.ResetLeft);
                    await client.SendAsync(resetCommand);
                    await Task.Delay(photoelectricConfig.ResetDelay);

                    command = PendulumCommands.Module2.SwingLeft;
                    pendulumState.SetSwing();
                }
            }
            else if (ShouldSwingRight(targetSlot))
            {
                if (currentState == "Reset")
                {
                    command = PendulumCommands.Module2.SwingRight;
                    pendulumState.SetSwing();
                }
                else if (pendulumState.LastSlot != targetSlot)
                {
                    // 先回正再摆动
                    var resetCommand = GetCommandBytes(PendulumCommands.Module2.ResetRight);
                    await client.SendAsync(resetCommand);
                    await Task.Delay(photoelectricConfig.ResetDelay);

                    command = PendulumCommands.Module2.SwingRight;
                    pendulumState.SetSwing();
                }
            }
            else if (currentState == "Swing") // 其他格口，不需要摆动
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
                await client.SendAsync(commandBytes);
                Log.Debug("已发送分拣命令到分拣光电 {Name}: {Command}", photoelectricName, command);
            }

            pendulumState.UpdateLastSlot(targetSlot);
            Log.Information("包裹 {Barcode} 分拣完成，目标格口: {TargetSlot}", package.Barcode, targetSlot);

            // 创建定时器，在指定延迟后回正
            if ((ShouldSwingLeft(targetSlot) || ShouldSwingRight(targetSlot)) && 
                currentState == "Reset") // 只有摆动了才需要回正
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
                        // 检查待分拣队列中的下一个包裹
                        var nextPackage = PendingSortPackages.Values
                            .Where(p => 
                                // 确保不是当前包裹
                                p.Index != package.Index &&
                                // 确保不在处理中的包裹列表里
                                !ProcessingPackages.ContainsKey(p.Barcode) &&
                                // 确保是这个分拣光电负责的格口
                                SlotBelongsToPhotoelectric(p.ChuteName, photoelectricName))
                            .OrderBy(p => p.TriggerTimestamp)
                            .ThenBy(p => p.Index)
                            .FirstOrDefault();

                        // 如果下一个包裹存在且目标格口与当前包裹相同，则不需要回正
                        if (nextPackage != null && nextPackage.ChuteName == targetSlot)
                        {
                            Log.Debug("下一个包裹 {Barcode} (序号: {Index}, 触发时间: {TriggerTime}) 目标格口与当前包裹相同 ({TargetSlot})，跳过回正",
                                nextPackage.Barcode, nextPackage.Index, nextPackage.TriggerTimestamp, targetSlot);
                            resetTimer.Dispose();
                            return;
                        }

                        // 如果没有下一个包裹或目标格口不同，则执行回正
                        if (!client.IsConnected()) return;
                        var resetCommand = ShouldSwingLeft(targetSlot)
                            ? GetCommandBytes(PendulumCommands.Module2.ResetLeft)
                            : GetCommandBytes(PendulumCommands.Module2.ResetRight);

                        await client.SendAsync(resetCommand);
                        pendulumState.SetReset();
                        Log.Debug("已发送回正命令到分拣光电 {Name}，当前无相同格口的待分拣包裹", photoelectricName);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "发送回正命令到分拣光电 {Name} 失败", photoelectricName);
                    }
                    finally
                    {
                        resetTimer.Dispose();
                    }
                };
                
                resetTimer.Start();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "执行分拣动作时发生错误");
        }
        finally
        {
            ProcessingPackages.TryRemove(package.Barcode, out _);
        }
    }

    /// <summary>
    /// 获取用于执行分拣动作的客户端
    /// </summary>
    protected virtual TcpClientService? GetSortingClient(string photoelectricName)
    {
        return TriggerClient; // 基类默认返回触发光电客户端，子类可以重写此方法
    }

    /// <summary>
    /// 判断是否需要向左摆动
    /// </summary>
    protected virtual bool ShouldSwingLeft(int targetSlot)
    {
        // 奇数格口向左摆动
        return targetSlot % 2 == 1;
    }

    /// <summary>
    /// 判断是否需要向右摆动
    /// </summary>
    protected virtual bool ShouldSwingRight(int targetSlot)
    {
        // 偶数格口向右摆动
        return targetSlot % 2 == 0;
    }
} 