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
    private readonly ConcurrentDictionary<string, ITcpClientService> _sortClients = new();
    private readonly Timer _timeoutCheckTimer;
    private readonly ITcpClientService _triggerClient;
    private readonly ConcurrentQueue<double> _triggerDelays = new();
    private readonly Queue<DateTime> _triggerTimes = new();
    private CancellationTokenSource? _cancellationTokenSource;
    private SortConfiguration _configuration = new();
    private bool _disposed;
    private bool _isRunning;

    public PendulumSortService(
        ITcpClientService triggerClient)
    {
        _triggerClient = triggerClient;
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
        _configuration = configuration;

        // 连接触发光电
        await _triggerClient.ConnectAsync(
            _configuration.TriggerPhotoelectric.IpAddress,
            _configuration.TriggerPhotoelectric.Port);

        // 连接所有分检光电并发送启动命令
        foreach (var sortPhotoelectric in _configuration.SortingPhotoelectrics)
        {
            var client = new TcpClientService();
            await client.ConnectAsync(sortPhotoelectric.IpAddress, sortPhotoelectric.Port);

            // 发送启动命令
            var startCommand = HexStringToByteArray(_moduleCommands["2代模块"].Start);
            await client.SendAsync(startCommand);

            // 添加到客户端字典
            _sortClients.TryAdd(sortPhotoelectric.Name, client);

            // 触发设备连接状态变更事件
            RaiseDeviceConnectionStatusChanged(sortPhotoelectric.Name, true);
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
                        var triggerData = await _triggerClient.ReceiveAsync();

                        // 处理触发光电数据
                        ProcessTriggerData(triggerData);

                        // 读取所有分检光电数据
                        foreach (var (name, client) in _sortClients)
                            try
                            {
                                var sortData = await client.ReceiveAsync();
                                await ProcessSortingPhotoelectricData(sortData, name);
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, "处理分拣光电 {Name} 数据时发生错误", name);
                            }

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

        await _cancellationTokenSource?.CancelAsync()!;
        _isRunning = false;

        try
        {
            // 向所有分检光电发送停止命令
            var stopCommand = HexStringToByteArray(_moduleCommands["2代模块"].Stop);
            foreach (var client in _sortClients.Values)
                try
                {
                    await client.SendAsync(stopCommand);
                    Log.Information("已发送停止命令到分检光电");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "发送停止命令时发生错误");
                }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "发送停止命令时发生错误");
        }

        // 断开所有连接
        await _triggerClient.DisconnectAsync();
        foreach (var client in _sortClients.Values) await client.DisconnectAsync();
        _sortClients.Clear();
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

            // 逐个检查时间戳
            while (_triggerTimes.Count > 0)
            {
                var timestamp = _triggerTimes.Dequeue();
                var delay = (now - timestamp).TotalMilliseconds;

                // 检查是否在触发光电配置的时间范围内
                if (!matchedTimestamp.HasValue &&
                    delay >= _configuration.TriggerPhotoelectric.TimeRangeLower &&
                    delay <= _configuration.TriggerPhotoelectric.TimeRangeUpper)
                {
                    // 找到匹配的时间戳
                    matchedTimestamp = timestamp;
                    // 设置处理时间（从触发光电到收到包裹数据的时间差）
                    package.ProcessingTime = delay;
                    Log.Debug("找到匹配的触发时间戳，延迟: {Delay}ms", delay);
                }
                else if (delay <= _configuration.TriggerPhotoelectric.TimeRangeUpper + 500)
                {
                    // 未匹配但未过期的时间戳放回队列
                    tempQueue.Enqueue(timestamp);
                    hasValidTriggers = true;
                }
                else
                {
                    Log.Debug("丢弃过期的触发时间戳: {Timestamp}", timestamp);
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

                        Log.Information("触发队列为空，使用历史中位延迟时间: {Delay}ms (基于 {Count} 个样本)", avgDelay, delays.Length);

                        // 使用中位延迟估算触发时间
                        var estimatedTime = now.AddMilliseconds(-avgDelay);
                        package.SetTriggerTimestamp(estimatedTime);
                        Log.Information("使用估算的触发时间: {Barcode}, 触发时间={Timestamp}, 估算延迟={Delay}ms",
                            package.Barcode, estimatedTime, avgDelay);
                    }
                    else
                    {
                        // 如果没有历史数据，使用配置中的默认延迟
                        var avgDelay = (_configuration.TriggerPhotoelectric.TimeRangeLower +
                                        _configuration.TriggerPhotoelectric.TimeRangeUpper) / 2.0;
                        Log.Warning("触发队列为空且无历史延迟数据，使用配置默认值: {Delay}ms", avgDelay);

                        var estimatedTime = now.AddMilliseconds(-avgDelay);
                        package.SetTriggerTimestamp(estimatedTime);
                        Log.Information("使用默认延迟估算触发时间: {Barcode}, 触发时间={Timestamp}, 默认延迟={Delay}ms",
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

                Log.Information("找到匹配的触发时间: {Barcode}, 触发时间={Timestamp}, 延迟={Delay}ms",
                    package.Barcode, matchedTimestamp.Value, actualDelay);
            }

            // 添加包裹到待分拣队列
            var photoelectricIndex = GetSortingPhotoelectricIndex(package.Index);
            if (photoelectricIndex >= _configuration.SortingPhotoelectrics.Count)
            {
                Log.Warning("包裹 {Barcode} (序号: {Index}) 的分拣光电索引超出范围",
                    package.Barcode, package.Index);
                return;
            }

            var timeRange = _configuration.SortingPhotoelectrics[photoelectricIndex].TimeRangeUpper;
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
            // 将接收到的数据转换为字符串
            var message = Encoding.ASCII.GetString(data);
            Log.Debug("收到触发光电数据: {Message}", message);

            // 检查是否是触发信号 (+OCCH1:1)
            if (!message.Contains("+OCCH1:1")) return;

            // 记录触发时间
            _triggerTimes.Enqueue(DateTime.Now);
            Log.Information("触发光电触发，当前队列长度: {Count}", _triggerTimes.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理触发光电数据时发生错误");
        }
    }

    /// <summary>
    ///     处理分检光电数据
    /// </summary>
    private async Task ProcessSortingPhotoelectricData(byte[] data, string photoelectricName)
    {
        try
        {
            // 将接收到的数据转换为字符串
            var message = Encoding.ASCII.GetString(data);
            Log.Debug("收到分拣光电数据: {Message}", message);

            // 检查是否是触发信号 (+OCCH1:1)
            if (!message.Contains("+OCCH1:1")) return;

            // 获取光电配置
            var photoelectric = _configuration.SortingPhotoelectrics
                .FirstOrDefault(p => p.Name == photoelectricName);
            if (photoelectric == null)
            {
                Log.Warning("未找到匹配的分拣光电配置: {Name}", photoelectricName);
                return;
            }

            var currentTime = DateTime.Now;

            // 在同步块内获取所有待分拣包裹
            List<PackageInfo> packages;
            lock (_packageLock)
            {
                packages = [.. _pendingSortPackages.Values];
                Log.Information("开始查找匹配包裹 - 光电:{Name}, 当前时间:{Time}, 待分拣队列数量:{Count}",
                    photoelectric.Name, currentTime, packages.Count);
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

                // 验证分拣光电是否匹配
                var expectedPhotoelectric = GetSortingPhotoelectricName(package.Index);
                if (expectedPhotoelectric != photoelectric.Name)
                {
                    Log.Debug("包裹 {Barcode}(序号:{Index}) 不属于当前光电 {PhotoName}，期望光电:{ExpectedName}",
                        package.Barcode, package.Index, photoelectric.Name, expectedPhotoelectric);
                    continue;
                }

                // 验证时间延迟
                var delay = (currentTime - package.TriggerTimestamp).TotalMilliseconds;
                if (delay < photoelectric.TimeRangeLower || delay > photoelectric.TimeRangeUpper)
                {
                    Log.Debug("包裹 {Barcode}(序号:{Index}) 时间延迟验证失败，延迟:{Delay}ms，允许范围:{Lower}-{Upper}ms",
                        package.Barcode, package.Index, delay, photoelectric.TimeRangeLower,
                        photoelectric.TimeRangeUpper);
                    continue;
                }

                Log.Information(
                    "光电 {Name} 与包裹 {Barcode} (序号: {Index}) 匹配成功，延迟: {Delay}ms",
                    photoelectric.Name, package.Barcode, package.Index, delay);

                // 将符合条件的包裹添加到处理列表
                packagesToProcess.Add(package);
            }

            if (packagesToProcess.Count == 0)
            {
                Log.Warning("光电 {Name} 未找到匹配的包裹，当前队列包裹数量: {Count}",
                    photoelectric.Name, packages.Count);
                return;
            }

            // 按触发时间排序，确保按正确顺序处理
            packagesToProcess = [.. packagesToProcess.OrderBy(p => p.TriggerTimestamp)];

            // 处理所有匹配的包裹
            foreach (var package in packagesToProcess)
            {
                // 标记包裹为处理中
                MarkPackageAsProcessing(package.Index, photoelectric.Name);

                // 执行分拣动作
                await ExecuteSortingAction(package, photoelectric.Name);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理分拣光电数据时发生错误");
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

            // 获取分拣命令
            var isRightSwing = package.Index % 2 == 0;
            var command = isRightSwing ? _moduleCommands["2代模块"].SwingRight : _moduleCommands["2代模块"].SwingLeft;

            // 获取摆轮状态
            if (!_pendulumStates.TryGetValue(photoelectricName, out var state))
            {
                state = new PendulumState(photoelectricName);
                _pendulumStates[photoelectricName] = state;
            }

            // 获取分拣光电配置
            var photoelectric = _configuration.SortingPhotoelectrics
                .FirstOrDefault(p => p.Name == photoelectricName);
            if (photoelectric == null)
            {
                Log.Error("未找到分拣光电配置: {Name}", photoelectricName);
                return;
            }

            // 等待包裹到达最佳分拣位置
            await Task.Delay(photoelectric.SortingDelay);

            // 执行分拣动作
            state.SetSwing();
            await _sortClients[photoelectric.Name].SendAsync(HexStringToByteArray(command));
            Log.Information("发送分拣命令: {Command} 到光电 {Name}, 包裹:{Barcode}, 方向:{Direction}",
                command, photoelectric.Name, package.Barcode, isRightSwing ? "右摆" : "左摆");

            // 延迟后检查是否需要回正
            await Task.Delay(photoelectric.ResetDelay);

            PackageInfo? nextPackage;
            // 在同步块内获取下一个包裹
            lock (_packageLock)
            {
                nextPackage = GetNextPackage(package);
            }

            // 检查下一个包裹是否使用相同索引
            var isSameSlot = nextPackage != null && nextPackage.Index % 2 == package.Index % 2;
            if (!isSameSlot)
            {
                var resetCommand =
                    isRightSwing ? _moduleCommands["2代模块"].ResetRight : _moduleCommands["2代模块"].ResetLeft;
                state.SetReset();
                await _sortClients[photoelectric.Name].SendAsync(HexStringToByteArray(resetCommand));
                Log.Information(
                    "发送回正命令: {Command} 到光电 {Name}, 原因:{Reason}, 当前状态:{State}",
                    resetCommand,
                    photoelectric.Name,
                    nextPackage == null ? "无后续包裹" : "不同格口",
                    state.GetCurrentState());
            }
            else
            {
                if (nextPackage != null)
                    Log.Information(
                        "跳过回正 - 光电 {Name} 保持 {Direction} 摆动状态，因为下一个包裹 {NextCode} 使用相同方向, 当前状态:{State}",
                        photoelectric.Name,
                        isRightSwing ? "右" : "左",
                        nextPackage.Barcode,
                        state.GetCurrentState());
            }

            // 更新摆轮最后处理的索引
            state.UpdateLastSlot(package.Index);
            Log.Debug("更新摆轮 {Name} 最后处理的索引为: {Index}", state.Name, state.LastSlot);

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
            var currentIndex = _pendingSortPackages.Keys.ToList().IndexOf(currentPackage.Index);
            if (currentIndex < 0) return null;

            // 从当前包裹之后查找第一个非处理中的包裹
            var nextPackages = _pendingSortPackages.Values
                .Skip(currentIndex + 1)
                .Where(p => p.TriggerTimestamp > currentPackage.TriggerTimestamp
                            && p.Index > currentPackage.Index
                            && !IsPackageProcessing(p.Index))
                .OrderBy(p => p.TriggerTimestamp)
                .ToList();

            if (nextPackages.Count == 0) return null;

            var nextPackage = nextPackages.First();
            Log.Debug(
                "找到下一个非处理中的包裹 - 当前包裹:{CurrentCode}(序号:{CurrentIndex}), " +
                "下一个包裹:{NextCode}(序号:{NextIndex})",
                currentPackage.Barcode, currentPackage.Index,
                nextPackage.Barcode, nextPackage.Index);
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

    /// <summary>
    ///     获取分拣光电名称
    /// </summary>
    private string? GetSortingPhotoelectricName(int index)
    {
        // 计算分拣光电索引（从0开始）
        // 例如：索引1-2对应第0个分拣光电，索引3-4对应第1个分拣光电，以此类推
        var photoelectricIndex = (index - 1) / 2;

        // 检查索引是否有效
        if (photoelectricIndex < _configuration.SortingPhotoelectrics.Count)
            return _configuration.SortingPhotoelectrics[photoelectricIndex].Name;

        Log.Warning("索引 {Index} 超出分拣光电数量范围", index);
        return null;
    }

    private async Task ReconnectAsync()
    {
        try
        {
            // 断开现有连接
            await _triggerClient.DisconnectAsync();
            foreach (var client in _sortClients.Values) await client.DisconnectAsync();
            _sortClients.Clear();

            // 重新连接触发光电
            await _triggerClient.ConnectAsync(
                _configuration.TriggerPhotoelectric.IpAddress,
                _configuration.TriggerPhotoelectric.Port);

            // 重新连接所有分检光电并发送启动命令
            foreach (var sortPhotoelectric in _configuration.SortingPhotoelectrics)
            {
                var client = new TcpClientService();
                await client.ConnectAsync(sortPhotoelectric.IpAddress, sortPhotoelectric.Port);

                // 发送启动命令
                var startCommand = HexStringToByteArray(_moduleCommands["2代模块"].Start);
                await client.SendAsync(startCommand);

                // 添加到客户端字典
                _sortClients.TryAdd(sortPhotoelectric.Name, client);

                // 触发设备连接状态变更事件
                RaiseDeviceConnectionStatusChanged(sortPhotoelectric.Name, true);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "重新连接时发生错误");
            throw;
        }
    }

    /// <summary>
    ///     获取分拣光电索引
    /// </summary>
    private static int GetSortingPhotoelectricIndex(int index)
    {
        // 计算分拣光电索引（从0开始）
        // 例如：索引1-2对应第0个分拣光电，索引3-4对应第1个分拣光电，以此类推
        return (index - 1) / 2;
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
            Log.Debug("准备触发设备状态变更事件: {DeviceName} -> {Status}", deviceName, connected ? "已连接" : "未连接");
            DeviceConnectionStatusChanged?.Invoke(this, (deviceName, connected));
            Log.Debug("设备状态变更事件已触发: {DeviceName} -> {Status}", deviceName, connected ? "已连接" : "未连接");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "触发设备状态变更事件失败: {DeviceName}", deviceName);
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _triggerClient.Dispose();
            foreach (var client in _sortClients.Values) client.Dispose();
            _sortClients.Clear();
            _cancellationTokenSource?.Dispose();
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

    private class PendulumState(string name)
    {
        private bool IsInReset { get; set; } = true;
        public int LastSlot { get; private set; }
        public string Name { get; } = name;

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