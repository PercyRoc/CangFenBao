using System.Collections.Concurrent;
using System.Text;
using System.Timers;
using CommonLibrary.Models;
using CommonLibrary.Models.Settings.Sort;
using Serilog;
using SortingService.Interfaces;
using Timer = System.Timers.Timer;

namespace SortingService.Services;

public class PendulumSortService : IPendulumSortService
{
    private const int MaxDelayHistory = 100; // 保存最近100个延迟数据
    private readonly ConcurrentDictionary<string, bool> _deviceConnectionStates = new();
    private readonly object _lockObject = new();

    private readonly Dictionary<string, PendulumCommands> _moduleCommands = new()
    {
        {
            "2代模块", new PendulumCommands
            {
                Start = "41 54 2B 53 54 41 43 48 31 3D 31 0D 0A",
                Stop = "41 54 2B 53 54 41 43 48 31 3D 30 0D 0A",
                SwingLeft = "41 54 2B 53 54 41 43 48 32 3D 31 0D 0A",
                ResetLeft = "41 54 2B 53 54 41 43 48 32 3D 30 0D 0A",
                SwingRight = "41 54 2B 53 54 41 43 48 33 3D 31 0D 0A",
                ResetRight = "41 54 2B 53 54 41 43 48 33 3D 30 0D 0A"
            }
        }
    };

    private readonly object _packageLock = new();
    private readonly ConcurrentDictionary<int, Timer> _packageTimers = new();
    private readonly ConcurrentDictionary<int, PackageInfo> _pendingSortPackages = new();
    private readonly ConcurrentDictionary<string, PendulumState> _pendulumStates = new();
    private readonly ConcurrentQueue<double> _processingDelays = new();
    private readonly ConcurrentDictionary<int, ProcessingStatus> _processingPackages = new();
    private readonly Timer _timeoutCheckTimer;
    private readonly ConcurrentQueue<double> _triggerDelays = new();
    private readonly Queue<DateTime> _triggerTimes = new();
    private CancellationTokenSource? _cancellationTokenSource;
    private SortConfiguration _configuration = new();
    private bool _disposed;
    private bool _isRunning;
    private TcpClientService? _triggerClient;

    public PendulumSortService()
    {
        // 初始化超时检查定时器
        _timeoutCheckTimer = new Timer(1000);
        _timeoutCheckTimer.Elapsed += CheckTimeoutPackages;
        _timeoutCheckTimer.AutoReset = true;
        _timeoutCheckTimer.Start();
    }

    /// <summary>
    ///     设备连接状态变更事件
    /// </summary>
    public event EventHandler<(string Name, bool Connected)>? DeviceConnectionStatusChanged;

    public async Task InitializeAsync(SortConfiguration configuration)
    {
        try
        {
            Log.Information("开始初始化分拣服务...");
            Log.Debug("触发光电配置: IP={IpAddress}, Port={Port}, 时间范围={Lower}-{Upper}ms",
                configuration.TriggerPhotoelectric.IpAddress,
                configuration.TriggerPhotoelectric.Port,
                configuration.TriggerPhotoelectric.TimeRangeLower,
                configuration.TriggerPhotoelectric.TimeRangeUpper);

            _configuration = configuration;

            // 初始化触发光电状态
            _deviceConnectionStates.TryAdd("触发光电", false);

            // 连接触发光电
            Log.Information("正在连接触发光电 {IpAddress}:{Port}...",
                _configuration.TriggerPhotoelectric.IpAddress,
                _configuration.TriggerPhotoelectric.Port);

            try
            {
                _triggerClient = new TcpClientService();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _triggerClient.ConnectAsync(
                    _configuration.TriggerPhotoelectric.IpAddress,
                    _configuration.TriggerPhotoelectric.Port);

                // 发送启动命令
                var startCommand = HexStringToByteArray(_moduleCommands["2代模块"].Start);
                await _triggerClient.SendAsync(startCommand);
                Log.Debug("已发送启动命令到触发光电");

                // 发送左右回正指令
                var resetLeftCommand = HexStringToByteArray(_moduleCommands["2代模块"].ResetLeft);
                var resetRightCommand = HexStringToByteArray(_moduleCommands["2代模块"].ResetRight);
                await _triggerClient.SendAsync(resetLeftCommand);
                await _triggerClient.SendAsync(resetRightCommand);
                Log.Debug("已发送左右回正命令到触发光电");

                // 更新触发光电状态
                UpdateDeviceConnectionState("触发光电", true);
            }
            catch (OperationCanceledException)
            {
                Log.Error("连接触发光电超时");
                UpdateDeviceConnectionState("触发光电", false);
                throw new TimeoutException("连接触发光电超时");
            }

            Log.Information("触发光电连接成功");
            Log.Information("分拣服务初始化完成");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化分拣服务失败");
            throw;
        }
    }

    public async Task StartAsync()
    {
        if (_isRunning) return;

        lock (_lockObject)
        {
            if (_isRunning) return;
            _isRunning = true;
            _cancellationTokenSource = new CancellationTokenSource();
        }

        // 启动分检服务的主循环
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_cancellationTokenSource.Token.IsCancellationRequested)
                    try
                    {
                        // 读取触发光电数据
                        var triggerData = await _triggerClient!.ReceiveAsync();

                        // 处理触发光电数据
                        ProcessTriggerData(triggerData);

                        await Task.Delay(100, _cancellationTokenSource.Token);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "处理数据时发生错误");

                        // 尝试重新连接
                        await ReconnectAsync();
                    }
            }
            catch (OperationCanceledException)
            {
                // 正常取消，不做处理
            }
            catch (Exception ex)
            {
                Log.Error(ex, "分检服务发生错误");
            }
            finally
            {
                _isRunning = false;
            }
        }, _cancellationTokenSource.Token);

        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!_isRunning) return;

        try
        {
            // 停止主循环
            await _cancellationTokenSource?.CancelAsync()!;
            _isRunning = false;

            // 停止超时检查定时器
            _timeoutCheckTimer.Stop();

            // 向触发光电发送停止命令
            if (_triggerClient != null)
                try
                {
                    // 发送左右回正指令
                    var resetLeftCommand = HexStringToByteArray(_moduleCommands["2代模块"].ResetLeft);
                    var resetRightCommand = HexStringToByteArray(_moduleCommands["2代模块"].ResetRight);
                    await _triggerClient.SendAsync(resetLeftCommand);
                    await _triggerClient.SendAsync(resetRightCommand);
                    Log.Debug("已发送左右回正命令到触发光电");

                    // 发送停止命令
                    var stopCommand = HexStringToByteArray(_moduleCommands["2代模块"].Stop);
                    await _triggerClient.SendAsync(stopCommand);
                    Log.Debug("已发送停止命令到触发光电");

                    await _triggerClient.DisconnectAsync();
                    _triggerClient.Dispose();
                    _triggerClient = null;
                    UpdateDeviceConnectionState("触发光电", false);
                    Log.Information("触发光电已断开连接");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "发送停止命令或断开触发光电连接时发生错误");
                }

            // 清理定时器资源
            foreach (var timer in _packageTimers.Values)
            {
                timer.Stop();
                timer.Dispose();
            }

            _packageTimers.Clear();

            // 清理其他资源
            lock (_packageLock)
            {
                _pendingSortPackages.Clear();
            }

            _processingPackages.Clear();
            _pendulumStates.Clear();
            _triggerTimes.Clear();
            while (_triggerDelays.TryDequeue(out _))
            {
            }

            while (_processingDelays.TryDequeue(out _))
            {
            }

            Log.Information("分拣服务已完全停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止分拣服务时发生错误");
            throw;
        }
    }

    public async Task UpdateConfigurationAsync(SortConfiguration configuration)
    {
        _configuration = configuration;

        // 重新连接所有设备
        await ReconnectAsync();
    }

    public bool IsRunning()
    {
        return _isRunning;
    }

    /// <summary>
    ///     处理收到的包裹信息
    /// </summary>
    /// <param name="package">包裹信息</param>
    public void ProcessPackage(PackageInfo package)
    {
        try
        {
            // 获取当前时间
            var now = DateTime.Now;

            // 查找匹配的触发时间戳，同时清理过期的时间戳
            DateTime? matchedTimestamp = null;
            var tempQueue = new Queue<DateTime>();
            var hasValidTriggers = false;

            var minDelay = _configuration.TriggerPhotoelectric.TimeRangeLower;
            var maxDelay = _configuration.TriggerPhotoelectric.TimeRangeUpper;

            Log.Debug("开始处理包裹 {Barcode}，当前触发队列长度: {Count}, 允许延迟范围: {Min}-{Max}ms",
                package.Barcode, _triggerTimes.Count, minDelay, maxDelay);

            // 逐个检查时间戳
            while (_triggerTimes.Count > 0)
            {
                var timestamp = _triggerTimes.Dequeue();
                var delay = (now - timestamp).TotalMilliseconds;

                Log.Debug("检查触发时间: {Time:HH:mm:ss.fff}, 延迟: {Delay}ms",
                    timestamp, delay);

                // 检查是否在触发光电配置的时间范围内
                if (!matchedTimestamp.HasValue &&
                    delay >= minDelay &&
                    delay <= maxDelay)
                {
                    // 找到匹配的时间戳
                    matchedTimestamp = timestamp;
                    package.ProcessingTime = delay;
                    Log.Information("找到匹配的触发时间戳，包裹: {Barcode}, 触发时间: {Time:HH:mm:ss.fff}, 延迟: {Delay}ms",
                        package.Barcode, timestamp, delay);
                }
                else if (delay <= maxDelay + 500) // 保留未过期的时间戳多等待500ms
                {
                    // 未匹配但未过期的时间戳放回队列
                    tempQueue.Enqueue(timestamp);
                    hasValidTriggers = true;
                    Log.Debug("保留未过期的触发时间: {Time:HH:mm:ss.fff}, 延迟: {Delay}ms",
                        timestamp, delay);
                }
                else
                {
                    Log.Debug("丢弃过期的触发时间: {Time:HH:mm:ss.fff}, 延迟: {Delay}ms",
                        timestamp, delay);
                }
            }

            // 将未匹配的非过期时间戳放回原队列
            while (tempQueue.Count > 0) _triggerTimes.Enqueue(tempQueue.Dequeue());

            if (!matchedTimestamp.HasValue)
            {
                if (hasValidTriggers)
                {
                    // 如果队列中有未过期的触发时间但没有匹配到，将包裹标记为异常
                    Log.Warning("触发队列中有数据但未找到匹配的时间戳，包裹 {Barcode} 将标记为异常", package.Barcode);
                    package.SetError("未找到匹配的触发时间");
                }
                else
                {
                    // 队列为空，使用历史延迟数据
                    var delays = _triggerDelays.ToArray();
                    if (delays.Length > 0)
                    {
                        // 使用中位数而不是平均值，避免异常值的影响
                        Array.Sort(delays);
                        var avgDelay = delays.Length % 2 == 0
                            ? (delays[delays.Length / 2 - 1] + delays[delays.Length / 2]) / 2
                            : delays[delays.Length / 2];

                        // 确保延迟在有效范围内
                        avgDelay = Math.Max(minDelay, Math.Min(maxDelay, avgDelay));

                        Log.Information("触发队列为空，使用历史中位延迟时间: {Delay}ms (基于 {Count} 个样本)",
                            avgDelay, delays.Length);

                        // 使用中位延迟估算触发时间
                        var estimatedTime = now.AddMilliseconds(-avgDelay);
                        package.SetTriggerTimestamp(estimatedTime);
                        package.ProcessingTime = avgDelay;
                        Log.Information("使用估算的触发时间: {Barcode}, 触发时间={Timestamp:HH:mm:ss.fff}, 估算延迟={Delay}ms",
                            package.Barcode, estimatedTime, avgDelay);
                    }
                    else
                    {
                        // 如果没有历史数据，使用配置中的默认延迟
                        var avgDelay = (minDelay + maxDelay) / 2.0;
                        Log.Warning("触发队列为空且无历史延迟数据，使用配置默认值: {Delay}ms", avgDelay);

                        var estimatedTime = now.AddMilliseconds(-avgDelay);
                        package.SetTriggerTimestamp(estimatedTime);
                        package.ProcessingTime = avgDelay;
                        Log.Information("使用默认延迟估算触发时间: {Barcode}, 触发时间={Timestamp:HH:mm:ss.fff}, 默认延迟={Delay}ms",
                            package.Barcode, estimatedTime, avgDelay);
                    }
                }
            }
            else
            {
                // 使用匹配的触发时间
                package.SetTriggerTimestamp(matchedTimestamp.Value);

                // 记录实际延迟时间
                var actualDelay = (now - matchedTimestamp.Value).TotalMilliseconds;
                _triggerDelays.Enqueue(actualDelay);

                // 保持队列大小不超过限制
                while (_triggerDelays.Count > MaxDelayHistory) _triggerDelays.TryDequeue(out _);

                Log.Information("找到匹配的触发时间: {Barcode}, 触发时间={Timestamp:HH:mm:ss.fff}, 延迟={Delay}ms",
                    package.Barcode, matchedTimestamp.Value, actualDelay);
            }

            // 验证最终的触发时间
            var finalDelay = (now - package.TriggerTimestamp).TotalMilliseconds;
            if (finalDelay < minDelay || finalDelay > maxDelay)
            {
                Log.Warning("最终延迟验证失败: {Barcode}, 延迟={Delay}ms, 允许范围={Min}-{Max}ms",
                    package.Barcode, finalDelay, minDelay, maxDelay);
                package.SetError($"延迟异常 ({finalDelay:F0}ms)");
                return;
            }

            // 添加包裹到待分拣队列
            var timeRange = _configuration.TriggerPhotoelectric.SortingTimeRangeUpper;
            if (TryAddPackageToQueue(package, timeRange)) return;
            package.SetError("添加到分拣队列失败");
            Log.Error("包裹 {Barcode} (序号: {Index}) 添加到分拣队列失败，已标记为异常",
                package.Barcode, package.Index);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹触发时间时发生错误: {Barcode}", package.Barcode);
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     获取设备连接状态
    /// </summary>
    /// <param name="deviceName">设备名称</param>
    /// <returns>true表示已连接，false表示未连接</returns>
    public bool GetDeviceConnectionState(string deviceName)
    {
        return _deviceConnectionStates.TryGetValue(deviceName, out var state) && state;
    }

    /// <summary>
    ///     获取所有设备的连接状态
    /// </summary>
    /// <returns>设备名称和连接状态的字典</returns>
    public Dictionary<string, bool> GetAllDeviceConnectionStates()
    {
        return new Dictionary<string, bool>(_deviceConnectionStates);
    }

    /// <summary>
    ///     将十六进制字符串转换为字节数组
    /// </summary>
    private static byte[] HexStringToByteArray(string hex)
    {
        return hex.Split(' ')
            .Select(x => Convert.ToByte(x, 16))
            .ToArray();
    }

    /// <summary>
    ///     处理触发光电数据
    /// </summary>
    private void ProcessTriggerData(byte[] data)
    {
        try
        {
            // 修改为独立判断两个信号（原先是if-else结构）
            var message = Encoding.ASCII.GetString(data);
            Log.Debug("收到触发光电数据: {Message}", message);

            // 使用独立if判断来处理可能同时到达的信号
            if (message.Contains("+OCCH1:1"))
            {
                // 记录触发时间
                var triggerTime = DateTime.Now;
                lock (_triggerTimes) // 添加同步锁
                {
                    _triggerTimes.Enqueue(triggerTime);
                    // 清理过期的触发时间（保留最近2秒的记录）
                    while (_triggerTimes.Count > 0 &&
                           (DateTime.Now - _triggerTimes.Peek()).TotalMilliseconds > 2000)
                        _triggerTimes.Dequeue();
                }

                Log.Information("触发光电触发，时间：{Time:HH:mm:ss.fff}, 当前队列长度: {Count}",
                    triggerTime, _triggerTimes.Count);
            }

            if (!message.Contains("+OCCH2:1")) return; // 改为独立if判断
            Log.Information("收到分拣触发信号，开始处理分拣逻辑");

            // 获取当前时间
            var currentTime = DateTime.Now;

            // 在同步块内获取所有待分拣包裹
            List<PackageInfo> packages;
            lock (_packageLock)
            {
                packages = [.. _pendingSortPackages.Values];
                Log.Information("开始查找匹配包裹 - 当前时间:{Time}, 待分拣队列数量:{Count}",
                    currentTime, packages.Count);
            }

            // 记录所有需要处理的包裹
            var packagesToProcess = new List<PackageInfo>();

            foreach (var package in packages)
            {
                Log.Debug("检查包裹匹配 - 包裹:{Barcode}, 序号:{Index}, 触发时间:{TriggerTime}",
                    package.Barcode, package.Index, package.TriggerTimestamp);

                // 检查包裹是否正在处理
                if (IsPackageProcessing(package.Index))
                {
                    Log.Debug("包裹 {Barcode}(序号:{Index}) 正在处理中，跳过",
                        package.Barcode, package.Index);
                    continue;
                }

                // 验证时间延迟
                var delay = (currentTime - package.TriggerTimestamp).TotalMilliseconds;
                if (delay < _configuration.TriggerPhotoelectric.SortingTimeRangeLower ||
                    delay > _configuration.TriggerPhotoelectric.SortingTimeRangeUpper)
                {
                    Log.Debug("包裹 {Barcode}(序号:{Index}) 分拣时间延迟验证失败，延迟:{Delay}ms，允许范围:{Lower}-{Upper}ms",
                        package.Barcode, package.Index, delay,
                        _configuration.TriggerPhotoelectric.SortingTimeRangeLower,
                        _configuration.TriggerPhotoelectric.SortingTimeRangeUpper);
                    continue;
                }

                Log.Information(
                    "分拣光电与包裹 {Barcode} (序号: {Index}) 匹配成功，延迟: {Delay}ms",
                    package.Barcode, package.Index, delay);

                // 将符合条件的包裹添加到处理列表
                packagesToProcess.Add(package);
            }

            if (packagesToProcess.Count == 0)
            {
                Log.Warning("未找到匹配的包裹，当前队列包裹数量: {Count}", packages.Count);
                return;
            }

            // 按触发时间和索引号排序，确保按正确顺序处理
            packagesToProcess = [.. packagesToProcess.OrderBy(p => p.TriggerTimestamp).ThenBy(p => p.Index)];

            // 处理所有匹配的包裹
            foreach (var package in packagesToProcess)
            {
                // 标记包裹为处理中
                MarkPackageAsProcessing(package.Index, "触发光电");

                // 执行分拣动作
                _ = ExecuteSortingAction(package, "触发光电");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理触发光电数据时发生错误");
        }
    }

    /// <summary>
    ///     执行分拣动作
    /// </summary>
    private async Task ExecuteSortingAction(PackageInfo package, string photoelectricName)
    {
        try
        {
            var startTime = DateTime.Now;
            Log.Debug("开始执行分拣动作 - 包裹:{Barcode}(序号:{Index}), 光电:{PhotoId}",
                package.Barcode, package.Index, photoelectricName);

            // 获取分拣命令
            // 修改分拣逻辑：1号格口左摆，2号格口右摆，3号格口不做动作
            string? command = null;
            var isNoAction = false;

            switch (package.ChuteName)
            {
                case 1: // 1号格口左摆
                    command = _moduleCommands["2代模块"].SwingLeft;
                    Log.Information("包裹 {Barcode} 分配到1号格口，执行左摆", package.Barcode);
                    break;
                case 2: // 2号格口右摆
                    command = _moduleCommands["2代模块"].SwingRight;
                    Log.Information("包裹 {Barcode} 分配到2号格口，执行右摆", package.Barcode);
                    break;
                case 3: // 3号格口不做动作
                    isNoAction = true;
                    Log.Information("包裹 {Barcode} 分配到3号格口，不做摆动", package.Barcode);
                    break;
                default: // 其他格口使用默认逻辑
                    if (package.ChuteName <= 0)
                    {
                        // 如果格口号为0或未设置，不执行动作
                        isNoAction = true;
                        Log.Information("包裹 {Barcode} 未设置有效格口号({ChuteName})，不做摆动",
                            package.Barcode, package.ChuteName);
                    }
                    else
                    {
                        // 其他格口根据奇偶性决定摆动方向
                        var isRightSwing = package.ChuteName % 2 == 0;
                        command = isRightSwing ? _moduleCommands["2代模块"].SwingRight : _moduleCommands["2代模块"].SwingLeft;
                        Log.Information("包裹 {Barcode} 分配到{Chute}号格口，执行{Direction}摆",
                            package.Barcode, package.ChuteName, isRightSwing ? "右" : "左");
                    }

                    break;
            }

            // 获取摆轮状态
            if (!_pendulumStates.TryGetValue(photoelectricName, out var state))
            {
                state = new PendulumState();
                _pendulumStates[photoelectricName] = state;
            }

            // 获取触发光电配置
            var photoelectric = _configuration.TriggerPhotoelectric;

            // 等待包裹到达最佳分拣位置
            if (photoelectric.SortingDelay > 0) await Task.Delay(photoelectric.SortingDelay);
            // 3号格口不执行动作
            if (!isNoAction)
            {
                // 执行分拣动作
                state.SetSwing();
                await _triggerClient!.SendAsync(HexStringToByteArray(command!));
                Log.Information("发送分拣命令: {Command} 到触发光电, 包裹:{Barcode}",
                    command, package.Barcode);
            }
            else
            {
                Log.Information("包裹 {Barcode} 分配到3号格口，跳过摆动命令", package.Barcode);
            }

            // 延迟后检查是否需要回正
            await Task.Delay(photoelectric.ResetDelay);

            PackageInfo? nextPackage;
            // 在同步块内获取下一个包裹
            lock (_packageLock)
            {
                // 确保使用当前正在处理的包裹查找下一个
                nextPackage = GetNextPackage(package);
                Log.Debug("查找下一个包裹结果 - 当前包裹:{CurrentCode}(序号:{CurrentIndex}), 下一个包裹:{NextCode}",
                    package.Barcode, package.Index,
                    nextPackage?.Barcode ?? "无");
            }

            // 如果当前是3号格口，不需要回正（因为没有摆动）
            if (!isNoAction)
            {
                // 确定当前摆动方向（用于回正判断）
                var isCurrentRightSwing = command == _moduleCommands["2代模块"].SwingRight;

                // 检查下一个包裹是否使用相同摆动方向
                var needReset = true;

                if (nextPackage != null)
                {
                    var nextIsNoAction = nextPackage.ChuteName == 3;
                    bool nextIsRightSwing;

                    switch (nextPackage.ChuteName)
                    {
                        case 1:
                            nextIsRightSwing = false; // 左摆
                            break;
                        case 2:
                            nextIsRightSwing = true; // 右摆
                            break;
                        case 3:
                            nextIsNoAction = true;
                            nextIsRightSwing = false; // 不重要，因为不会摆动
                            break;
                        default:
                            nextIsRightSwing = nextPackage.ChuteName % 2 == 0;
                            break;
                    }

                    // 如果下一个包裹不摆动，也要回正
                    // 如果下一个包裹摆动方向与当前相同，则不需要回正
                    needReset = nextIsNoAction || nextIsRightSwing != isCurrentRightSwing;
                }

                if (needReset)
                {
                    var resetCommand =
                        isCurrentRightSwing ? _moduleCommands["2代模块"].ResetRight : _moduleCommands["2代模块"].ResetLeft;
                    state.SetReset();
                    await _triggerClient!.SendAsync(HexStringToByteArray(resetCommand));
                    Log.Information(
                        "发送回正命令: {Command} 到触发光电, 原因:{Reason}, 当前状态:{State}",
                        resetCommand,
                        nextPackage == null ? "无后续包裹" : "不同格口方向",
                        state.GetCurrentState());
                }
                else
                {
                    if (nextPackage != null)
                        Log.Information(
                            "跳过回正 - 触发光电保持 {Direction} 摆动状态，因为下一个包裹 {NextCode} 使用相同方向, 当前状态:{State}",
                            isCurrentRightSwing ? "右" : "左",
                            nextPackage.Barcode,
                            state.GetCurrentState());
                }
            }

            // 更新摆轮最后处理的索引
            state.UpdateLastSlot(package.Index);
            Log.Debug("更新摆轮最后处理的索引为: {Index}", state.LastSlot);

            // 在同步块内移除已处理的包裹
            lock (_packageLock)
            {
                if (_pendingSortPackages.TryRemove(package.Index, out _))
                {
                    Log.Information("包裹已完成分拣，从队列中移除: {Barcode}(序号:{Index})",
                        package.Barcode, package.Index);

                    // 计算并记录处理延迟时间
                    var processingTime = (DateTime.Now - startTime).TotalMilliseconds;
                    _processingDelays.Enqueue(processingTime);
                    while (_processingDelays.Count > MaxDelayHistory) _processingDelays.TryDequeue(out _);
                    Log.Debug("记录包裹处理延迟时间: {Barcode}, 延迟={Delay}ms", package.Barcode, processingTime);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "执行分拣动作时发生错误 - 包裹:{Barcode}(序号:{Index})",
                package.Barcode, package.Index);
        }
        finally
        {
            _processingPackages.TryRemove(package.Index, out _);
        }
    }

    /// <summary>
    ///     获取下一个待处理的包裹
    /// </summary>
    private PackageInfo? GetNextPackage(PackageInfo currentPackage)
    {
        lock (_packageLock)
        {
            Log.Debug("开始查找下一个包裹 - 当前包裹:{CurrentCode}(序号:{CurrentIndex}), 当前队列数量:{Count}",
                currentPackage.Barcode, currentPackage.Index, _pendingSortPackages.Count);

            // 直接从字典中获取所有值并排序
            var nextPackages = _pendingSortPackages.Values
                .Where(p => p.TriggerTimestamp > currentPackage.TriggerTimestamp
                            && p.Index > currentPackage.Index
                            && !IsPackageProcessing(p.Index))
                .OrderBy(p => p.TriggerTimestamp)
                .ToList();

            if (nextPackages.Count == 0)
            {
                Log.Debug("未找到符合条件的下一个包裹 - 当前包裹:{CurrentCode}(序号:{CurrentIndex})",
                    currentPackage.Barcode, currentPackage.Index);
                return null;
            }

            var nextPackage = nextPackages.First();
            Log.Debug(
                "找到下一个非处理中的包裹 - 当前包裹:{CurrentCode}(序号:{CurrentIndex}), " +
                "下一个包裹:{NextCode}(序号:{NextIndex}), 触发时间差:{TimeDiff}ms",
                currentPackage.Barcode, currentPackage.Index,
                nextPackage.Barcode, nextPackage.Index,
                (nextPackage.TriggerTimestamp - currentPackage.TriggerTimestamp).TotalMilliseconds);
            return nextPackage;
        }
    }

    private bool IsPackageProcessing(int packageIndex)
    {
        if (!_processingPackages.TryGetValue(packageIndex, out var status) || !status.IsProcessing) return false;
        var processingTime = (DateTime.Now - status.StartTime).TotalMilliseconds;
        Log.Debug("包裹处理状态 - 序号:{Index}, 光电:{PhotoId}, 已处理时间:{ProcessingTime}ms",
            packageIndex, status.PhotoelectricId, processingTime);
        return true;
    }

    private void MarkPackageAsProcessing(int packageIndex, string photoelectricId)
    {
        var status = new ProcessingStatus
        {
            StartTime = DateTime.Now,
            IsProcessing = true,
            PhotoelectricId = photoelectricId
        };

        _processingPackages.AddOrUpdate(
            packageIndex,
            _ => status,
            (_, _) => status);

        Log.Debug("包裹开始处理 - 序号:{Index}, 光电:{PhotoId}, 开始时间:{StartTime}",
            packageIndex, status.PhotoelectricId, status.StartTime);
    }

    private async Task ReconnectAsync()
    {
        try
        {
            Log.Information("开始重新连接设备...");

            // 断开现有连接
            await _triggerClient!.DisconnectAsync();
            UpdateDeviceConnectionState("触发光电", false);

            // 重新连接触发光电
            Log.Information("正在重新连接触发光电 {IpAddress}:{Port}...",
                _configuration.TriggerPhotoelectric.IpAddress,
                _configuration.TriggerPhotoelectric.Port);

            try
            {
                _triggerClient = new TcpClientService();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _triggerClient.ConnectAsync(
                    _configuration.TriggerPhotoelectric.IpAddress,
                    _configuration.TriggerPhotoelectric.Port);

                // 发送启动命令
                var startCommand = HexStringToByteArray(_moduleCommands["2代模块"].Start);
                await _triggerClient.SendAsync(startCommand);
                Log.Debug("已发送启动命令到触发光电");

                // 发送左右回正指令
                var resetLeftCommand = HexStringToByteArray(_moduleCommands["2代模块"].ResetLeft);
                var resetRightCommand = HexStringToByteArray(_moduleCommands["2代模块"].ResetRight);
                await _triggerClient.SendAsync(resetLeftCommand);
                await _triggerClient.SendAsync(resetRightCommand);
                Log.Debug("已发送左右回正命令到触发光电");

                // 更新触发光电状态
                UpdateDeviceConnectionState("触发光电", true);
                Log.Information("触发光电重新连接成功");
            }
            catch (OperationCanceledException)
            {
                Log.Error("重新连接触发光电超时");
                UpdateDeviceConnectionState("触发光电", false);
                throw new TimeoutException("重新连接触发光电超时");
            }

            Log.Information("所有设备重新连接完成");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "重新连接时发生错误");
            throw;
        }
    }

    /// <summary>
    ///     检查超时包裹
    /// </summary>
    private void CheckTimeoutPackages(object? sender, ElapsedEventArgs e)
    {
        try
        {
            lock (_packageLock)
            {
                // 检查并清理过期的定时器
                foreach (var kvp in _packageTimers.ToList().Where(kvp => !_pendingSortPackages.ContainsKey(kvp.Key)))
                {
                    if (!_packageTimers.TryRemove(kvp.Key, out var timer)) continue;
                    timer.Dispose();
                    Log.Debug("清理无效定时器: 序号={Index}", kvp.Key);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "检查超时包裹时发生错误");
        }
    }

    /// <summary>
    ///     添加包裹到待分拣队列
    /// </summary>
    private bool TryAddPackageToQueue(PackageInfo package, int timeRange)
    {
        lock (_packageLock)
        {
            if (!_pendingSortPackages.TryAdd(package.Index, package))
            {
                Log.Warning("包裹添加到待分拣队列失败: {Barcode}, 序号={Index}, 可能已存在",
                    package.Barcode, package.Index);
                return false;
            }

            Log.Information("包裹已添加到待分拣队列: {Barcode}, 序号={Index}, 当前队列长度={Count}",
                package.Barcode, package.Index, _pendingSortPackages.Count);

            // 设置包裹超时定时器
            var timer = new Timer(timeRange + 500);
            timer.Elapsed += (_, _) =>
            {
                lock (_packageLock)
                {
                    if (_pendingSortPackages.TryRemove(package.Index, out _))
                        Log.Warning("包裹 {Barcode} (序号: {Index}) 已超时，从队列中移除",
                            package.Barcode, package.Index);
                }

                timer.Dispose();
            };
            _packageTimers.TryAdd(package.Index, timer);
            timer.Start();
            Log.Debug("已设置包裹超时定时器: {Barcode}, 超时时间={Timeout}ms",
                package.Barcode, timeRange + 500);

            return true;
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
            Log.Error(ex, "触发设备状态变更事件失败: {DeviceName}", deviceName);
        }
    }

    private void UpdateDeviceConnectionState(string deviceName, bool isConnected)
    {
        _deviceConnectionStates.AddOrUpdate(
            deviceName,
            isConnected,
            (_, _) => isConnected);

        // 触发设备连接状态变更事件
        RaiseDeviceConnectionStatusChanged(deviceName, isConnected);

        Log.Debug("设备 {Name} 连接状态更新为: {Status}",
            deviceName,
            isConnected ? "已连接" : "已断开");
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
            try
            {
                // 确保服务停止
                if (_isRunning)
                {
                    Log.Warning("检测到服务未停止，正在强制停止");
                    StopAsync().Wait();
                }

                // 释放资源
                _triggerClient?.Dispose();

                _cancellationTokenSource?.Dispose();
                _timeoutCheckTimer.Dispose();

                foreach (var timer in _packageTimers.Values) timer.Dispose();
                _packageTimers.Clear();

                // 清空设备状态
                _deviceConnectionStates.Clear();

                Log.Information("分拣服务资源已完全释放");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "释放分拣服务资源时发生错误");
            }

        _disposed = true;
    }

    private class ProcessingStatus
    {
        public DateTime StartTime { get; init; }
        public bool IsProcessing { get; init; }
        public string PhotoelectricId { get; init; } = string.Empty;
    }

    private class PendulumCommands
    {
        public string Start { get; init; } = string.Empty;
        public string Stop { get; init; } = string.Empty;
        public string SwingLeft { get; init; } = string.Empty;
        public string ResetLeft { get; init; } = string.Empty;
        public string SwingRight { get; init; } = string.Empty;
        public string ResetRight { get; init; } = string.Empty;
    }

    private class PendulumState
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
            return IsInReset ? "回正" : "摆动";
        }
    }
}