using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Common.Models.Package;
using Common.Services.Settings;
using DongtaiFlippingBoardMachine.Events;
using DongtaiFlippingBoardMachine.Models;
using EasyModbus;
using Prism.Events;
using Serilog;

namespace DongtaiFlippingBoardMachine.Services;

/// <summary>
///     分拣服务
/// </summary>
public class SortingService : IDisposable
{
    private const int CounterHistoryCapacity = 2048;
    private const double PulseEmaAlpha = 0.3; // 指数平滑系数

    private const int FineAdjustWindowMs = 5; // 微调提前窗口(ms)

    // 记录 Modbus 边沿历史 (计数值, 时间戳)
    private readonly ConcurrentQueue<(uint Counter, DateTime Timestamp)> _counterHistory = new();
    private readonly IEventAggregator _eventAggregator;
    private readonly ConcurrentDictionary<PackageInfo, uint> _packagePendingDistances = new();
    private readonly ConcurrentQueue<PackageInfo> _packageQueue = new();
    private readonly ConcurrentDictionary<PackageInfo, uint> _packageTargetCounters = new();
    private readonly Dictionary<string, TcpConnectionConfig> _tcpConfigs = [];
    private readonly ITcpConnectionService _tcpConnectionService;
    private readonly IZtoSortingService _ztoSortingService;
    private bool _disposed;
    private DateTime _lastCounterUpdateTime;
    private double _lastIntervalMs;

    private ModbusClient? _modbusClient;

    // Modbus 轮询所需字段
    private CancellationTokenSource? _modbusPollingCts;
    private double _smoothedPulseMs; // 平滑后的单脉冲时长

    public SortingService(ITcpConnectionService tcpConnectionService,
        ISettingsService settingsService,
        IZtoSortingService ztoSortingService,
        IEventAggregator eventAggregator)
    {
        _tcpConnectionService = tcpConnectionService;
        _ztoSortingService = ztoSortingService;
        _eventAggregator = eventAggregator;

        Settings = settingsService.LoadSettings<PlateTurnoverSettings>();

        // 订阅配置更新事件
        _eventAggregator.GetEvent<PlateTurnoverSettingsUpdatedEvent>().Subscribe(OnSettingsUpdated);

        // 初始化TCP配置
        UpdateTcpConfigs();

        // 始终使用 Modbus 轮询（固定地址，不再读取配置）
        StartModbusPolling("192.168.1.50", 502);
    }

    private PlateTurnoverSettings Settings { get; set; }

    /// <summary>
    ///     获取最近一次计算的高电平信号时间间隔（毫秒）
    /// </summary>
    private double LastIntervalMs =>
        // 如果 _lastIntervalMs 尚未被有效计算（仍为0或初始值），则使用默认间隔
        // 增加一个检查确保返回的是合理的值
        _lastIntervalMs > 0 ? _lastIntervalMs : Settings.DefaultInterval;

    /// <summary>
    ///     当前 Modbus 计数（32位无符号）
    /// </summary>
    public uint CurrentModbusCounter { get; private set; }

    /// <summary>
    ///     Modbus 连接状态
    /// </summary>
    public bool IsModbusConnected => _modbusClient?.Connected ?? false;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     添加包裹到分拣队列
    /// </summary>
    /// <param name="package">包裹信息</param>
    internal void EnqueuePackage(PackageInfo package)
    {
        try
        {
            // 获取包裹对应的翻板机配置
            var turnoverItem = Settings.Items.FirstOrDefault(item => item.MappingChute == package.ChuteNumber);
            if (turnoverItem == null)
            {
                Log.Warning("包裹 {Barcode} 的目标格口 {ChuteNumber} 未找到对应的翻板机配置", package.Barcode, package.ChuteNumber);
                // 如果找不到对应的翻板机配置，分配到异常格口
                package.SetChute(Settings.ErrorChute);
            }
            else
            {
                Log.Information("包裹 {Barcode} 目标距离(脉冲): {Distance}", package.Barcode, turnoverItem.Distance);
            }

            // 记录 Modbus 目标计数: 从触发后的下一脉冲开始数
            var distancePulses = (uint)Math.Round(turnoverItem?.Distance ?? 0, MidpointRounding.AwayFromZero);
            if (_lastCounterUpdateTime == DateTime.MinValue)
            {
                // 尚未获得 Modbus 计数，等首次轮询后补记
                _packagePendingDistances[package] = distancePulses;
                Log.Debug("包裹 {Barcode} 在首次Modbus更新后将设置目标计数 (+{Distance})", package.Barcode, distancePulses);
            }
            else
            {
                var history = _counterHistory.ToArray();
                var nextIdx = -1;
                var nowForEdge = DateTime.Now;
                for (var i = 0; i < history.Length; i++)
                    if (history[i].Timestamp >= nowForEdge)
                    {
                        nextIdx = i;
                        break;
                    }

                // 基于“当前时间距触发时间”与单脉冲时长的关系，决定是否在下一边沿基础上再 +1
                var effectivePulseMs = _smoothedPulseMs > 0 ? _smoothedPulseMs : LastIntervalMs;
                var elapsedSinceTriggerMs = (DateTime.Now - package.TriggerTimestamp).TotalMilliseconds;
                var shiftPulses = elapsedSinceTriggerMs >= effectivePulseMs ? 1u : 0u;
                uint target;
                uint logStartCounter;
                DateTime? refEdgeTime;
                if (nextIdx >= 0)
                {
                    var nextCounter = history[nextIdx].Counter;
                    var kMinusOne = distancePulses > 0 ? distancePulses - 1u : 0u;
                    target = unchecked(nextCounter + kMinusOne + shiftPulses);
                    logStartCounter = unchecked(nextCounter + shiftPulses); // shift=1 表示从“下一边沿”的再下一拍起算
                    refEdgeTime = history[nextIdx].Timestamp;
                }
                else
                {
                    // next 尚未到达：按照“始终从下一边沿起算”的语义，目标=prev+distance；若超一拍（shiftPulses==1）则再+1
                    target = unchecked(CurrentModbusCounter + distancePulses + shiftPulses);
                    logStartCounter = unchecked(CurrentModbusCounter + 1u + shiftPulses);
                    refEdgeTime = null; // 无法可靠给出下一边沿时间
                }

                _packageTargetCounters[package] = target;
                Log.Information(
                    refEdgeTime.HasValue
                        ? "包裹 {Barcode} 记录目标计数: {Target} (起算脉冲={Start} 基准边沿时间={Edge:o} 距离={Distance} shift={Shift} elapsedSinceTriggerMs={Elapsed:F2} perPulseMs={Pulse:F2})，Trigger={Trigger:o}"
                        : "包裹 {Barcode} 记录目标计数: {Target} (回退: 起算脉冲={Start} 距离={Distance} shift={Shift} elapsedSinceTriggerMs={Elapsed:F2} perPulseMs={Pulse:F2})，Trigger={Trigger:o}",
                    package.Barcode, target, logStartCounter, refEdgeTime ?? default, distancePulses, shiftPulses,
                    elapsedSinceTriggerMs, effectivePulseMs, package.TriggerTimestamp);
            }

            package.SetStatus(PackageStatus.Success);
            _packageQueue.Enqueue(package);
            Log.Information("包裹 {Barcode} 已添加到分拣队列，目标格口：{ChuteNumber}", package.Barcode, package.ChuteNumber);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "添加包裹到分拣队列时发生错误：{Barcode}", package.Barcode);
            throw;
        }
    }


    /// <summary>
    ///     接收来自UI的实时配置更新
    /// </summary>
    /// <param name="newSettings">新的配置对象</param>
    private void OnSettingsUpdated(PlateTurnoverSettings newSettings)
    {
        Log.Information("接收到来自UI的实时配置更新...");
        Settings = newSettings;
        UpdateTcpConfigs();
        // 不再根据配置变更重启 Modbus（固定地址）
        Log.Information("翻板机实时配置已应用。");
    }

    /// <summary>
    ///     根据当前配置更新TCP连接字典
    /// </summary>
    private void UpdateTcpConfigs()
    {
        var currentKnownIps = _tcpConfigs.Keys.ToHashSet();
        var newSettingsIps = Settings.Items
            .Where(item => !string.IsNullOrEmpty(item.TcpAddress))
            .Select(item => item.TcpAddress!)
            .ToHashSet();

        // 找出需要新增的
        var ipsToAdd = newSettingsIps.Where(ip => !currentKnownIps.Contains(ip)).ToList();
        foreach (var ip in ipsToAdd)
        {
            _tcpConfigs[ip] = new TcpConnectionConfig(ip, 2000);
            Log.Information("已添加新的TCP配置: {IpAddress}", ip);
        }

        // 找出需要移除的
        var ipsToRemove = currentKnownIps.Where(ip => !newSettingsIps.Contains(ip)).ToList();
        foreach (var ip in ipsToRemove)
        {
            _tcpConfigs.Remove(ip);
            Log.Information("已移除过时的TCP配置: {IpAddress}", ip);
        }

        if (ipsToAdd.Count != 0 || ipsToRemove.Count != 0)
            Log.Information("TCP配置更新完成. 新增: {AddCount}, 移除: {RemoveCount}", ipsToAdd.Count, ipsToRemove.Count);
    }

    // 按计数过线触发分拣：如果包裹目标计数位于 [prevCounter, newCounter] 区间内，则在对应时间点 + 延迟后执行落格
    private void CheckAndTriggerByCounter(uint prevCounter, uint newCounter, DateTime windowStartTime,
        double perPulseMs)
    {
        try
        {
            if (_packageTargetCounters.IsEmpty) return;

            // 处理溢出：统一转换到64位并判断区间
            ulong start = prevCounter;
            var end = newCounter >= prevCounter
                ? newCounter
                : uint.MaxValue + 1UL + newCounter; // 跨越溢出

            var toTrigger = new List<(PackageInfo Package, uint Target)>();
            foreach (var kv in _packageTargetCounters)
            {
                var t = kv.Value;
                var ut = t >= prevCounter ? t : uint.MaxValue + 1UL + t; // 与区间同处理
                if (ut > start && ut <= end) toTrigger.Add((kv.Key, kv.Value));
            }

            if (toTrigger.Count == 0) return;

            foreach (var (package, target) in toTrigger)
            {
                // 计算对应的理论到达时间点
                var pulsesFromStart = target >= prevCounter
                    ? target - prevCounter
                    : (ulong)uint.MaxValue - prevCounter + 1UL + target;
                var arrivalTime = windowStartTime + TimeSpan.FromMilliseconds(pulsesFromStart * perPulseMs);
                var nowTime = DateTime.Now;
                var roughDelay = arrivalTime > nowTime ? arrivalTime - nowTime : TimeSpan.Zero;

                // 取包裹配置参数快照
                var turnoverItem = Settings.Items.FirstOrDefault(item => item.MappingChute == package.ChuteNumber);
                if (turnoverItem == null) continue;

                var delayMs = (int)(LastIntervalMs * turnoverItem.DelayFactor);
                var magnetTime = turnoverItem.MagnetTime;
                var tcpAddress = turnoverItem.TcpAddress;
                var ioPoint = turnoverItem.IoPoint;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var arriveDeltaMs = (arrivalTime - package.TriggerTimestamp).TotalMilliseconds;
                        Log.Information(
                            "分拣调度: 包裹={Barcode} 目标计数={Target} 起始={Start} 结束={End} 跨越={Pulses} 到达={Arrival:o} perPulse={Per:F2}ms delayMs={Delay} arriveVsTriggerMs={ArriveDeltaMs:F2} trigger={Trigger:o} chute={Chute} tcp={Tcp} io={Io}",
                            package.Barcode,
                            target,
                            prevCounter,
                            newCounter,
                            pulsesFromStart,
                            arrivalTime,
                            perPulseMs,
                            delayMs,
                            arriveDeltaMs,
                            package.TriggerTimestamp,
                            package.ChuteNumber,
                            tcpAddress,
                            ioPoint);
                        // 等到理论到达时间（粗等待）
                        if (roughDelay > TimeSpan.Zero)
                        {
                            var coarse = roughDelay - TimeSpan.FromMilliseconds(FineAdjustWindowMs);
                            if (coarse > TimeSpan.Zero) await Task.Delay(coarse);
                            // 微调自旋，尽量贴近目标时刻
                            var sw = Stopwatch.StartNew();
                            while (sw.ElapsedMilliseconds < FineAdjustWindowMs) await Task.Yield();
                        }

                        // 再按延迟系数等待
                        if (delayMs > 0)
                        {
                            // 两段式：先等待 delayMs - FineAdjustWindowMs，再微调
                            if (delayMs > FineAdjustWindowMs)
                            {
                                await Task.Delay(delayMs - FineAdjustWindowMs);
                                var sw2 = Stopwatch.StartNew();
                                while (sw2.ElapsedMilliseconds < FineAdjustWindowMs) await Task.Yield();
                            }
                            else
                            {
                                await Task.Delay(delayMs);
                            }
                        }

                        if (string.IsNullOrEmpty(tcpAddress))
                        {
                            Log.Error("包裹 {Barcode} 的翻板机未配置TCP地址", package.Barcode);
                            return;
                        }

                        if (!_tcpConfigs.TryGetValue(tcpAddress, out var targetConfig))
                        {
                            Log.Error("包裹 {Barcode} 配置的TCP地址 {TcpAddress} 在配置字典中未找到", package.Barcode, tcpAddress);
                            return;
                        }

                        var outNumber = ioPoint!.ToUpper().Replace("OUT", "");
                        var lockCommand = $"AT+STACH{outNumber}=1\r\n";
                        var commandData = Encoding.ASCII.GetBytes(lockCommand);

                        Log.Information("准备向TCP模块 {TargetIp} 发送落格命令 {Command} (包裹 {Barcode})",
                            targetConfig.IpAddress, lockCommand.TrimEnd('\r', '\n'), package.Barcode);

                        await _tcpConnectionService.SendToTcpModuleAsync(targetConfig, commandData);
                        Log.Information("包裹 {Barcode} 已发送落格命令：{Command} 到 {TargetIp}", package.Barcode,
                            lockCommand.TrimEnd('\r', '\n'), targetConfig.IpAddress);

                        await Task.Delay(magnetTime);
                        var resetCommand = $"AT+STACH{outNumber}=0\r\n";
                        var resetData = Encoding.ASCII.GetBytes(resetCommand);

                        Log.Information("准备向TCP模块 {TargetIp} 发送复位命令 {Command} (包裹 {Barcode})",
                            targetConfig.IpAddress, resetCommand.TrimEnd('\r', '\n'), package.Barcode);

                        await _tcpConnectionService.SendToTcpModuleAsync(targetConfig, resetData);
                        Log.Information("包裹 {Barcode} 已发送复位命令：{Command} 到 {TargetIp}", package.Barcode,
                            resetCommand.TrimEnd('\r', '\n'), targetConfig.IpAddress);

                        _ = ReportSortingResultAsync(package);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "包裹 {Barcode} 发送落格命令时发生错误", package.Barcode);
                    }
                });

                // 从队列移除该包裹
                _packageTargetCounters.TryRemove(package, out _);
                // 从逻辑队列移除
                // 重建队列（仅保留未触发的包裹）
                var remainingPackages = _packageQueue.Where(p => p != package);
                var newQueue = new ConcurrentQueue<PackageInfo>(remainingPackages);
                while (_packageQueue.TryDequeue(out _))
                {
                }

                foreach (var p in newQueue) _packageQueue.Enqueue(p);

                Log.Information("包裹 {Barcode} 已达到目标计数并从队列中移除", package.Barcode);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "按计数触发分拣时发生错误");
        }
    }

    private async Task ReportSortingResultAsync(PackageInfo package)
    {
        try
        {
            if (string.IsNullOrEmpty(Settings.ZtoPipelineCode)) return;

            // === 修改：使用包裹的最终独立计数上报 ===
            await _ztoSortingService.ReportSortingResultAsync(
                package,
                Settings.ZtoPipelineCode,
                1,
                Settings.ZtoTrayCode);

            Log.Information("包裹 {Barcode} 分拣结果已上报到中通系统", package.Barcode);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "上报分拣结果到中通系统时发生错误: {Barcode}", package.Barcode);
        }
    }

    /// <summary>
    ///     向所有已配置的格口发送复位命令。
    /// </summary>
    public async Task ResetAllChutesAsync()
    {
        Log.Information("正在向所有已配置的格口发送复位命令...");
        var allItems = Settings.Items.ToList(); // 创建副本以安全迭代
        if (allItems.Count == 0)
        {
            Log.Information("没有配置任何格口，跳过复位操作。");
            return;
        }

        var resetTasks = allItems
            .Where(item => !string.IsNullOrEmpty(item.TcpAddress) && !string.IsNullOrEmpty(item.IoPoint))
            .Select(item => Task.Run(async () =>
            {
                try
                {
                    if (!_tcpConfigs.TryGetValue(item.TcpAddress!, out var targetConfig))
                    {
                        Log.Error("格口 {MappingChute} 配置的TCP地址 {TcpAddress} 在配置字典中未找到，无法发送复位命令", item.MappingChute,
                            item.TcpAddress);
                        return;
                    }

                    if (!_tcpConnectionService.TcpModuleClients.TryGetValue(targetConfig, out var client) ||
                        !client.Connected)
                    {
                        Log.Warning("TCP模块 {IpAddress} 未连接，无法为格口 {MappingChute} 发送复位命令", targetConfig.IpAddress,
                            item.MappingChute);
                        return;
                    }

                    var outNumber = item.IoPoint!.ToUpper().Replace("OUT", "");
                    var resetCommand = $"AT+STACH{outNumber}=0\r\n";
                    var resetData = Encoding.ASCII.GetBytes(resetCommand);

                    Log.Information("向TCP模块 {TargetIp} (格口 {Chute}) 发送复位命令: {Command}",
                        targetConfig.IpAddress, item.MappingChute, resetCommand.TrimEnd('\r', '\n'));

                    await _tcpConnectionService.SendToTcpModuleAsync(targetConfig, resetData);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "为格口 {MappingChute} (TCP: {TcpAddress}, IO: {IoPoint}) 发送复位命令时发生错误",
                        item.MappingChute, item.TcpAddress, item.IoPoint);
                }
            }));

        await Task.WhenAll(resetTasks);
        Log.Information("所有格口的复位命令已发送完毕。");
    }

    private void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            _eventAggregator.GetEvent<PlateTurnoverSettingsUpdatedEvent>().Unsubscribe(OnSettingsUpdated);
            StopModbusPolling();
        }

        _disposed = true;
    }

    // ================== Modbus TCP 轮询实现 ==================
    private void StartModbusPolling(string ip, int port)
    {
        try
        {
            StopModbusPolling();
            _modbusPollingCts = new CancellationTokenSource();
            var token = _modbusPollingCts.Token;
            _ = Task.Run(async () =>
            {
                Log.Information("Modbus 轮询启动，目标 {Ip}:{Port}，读取保持寄存器 0 和 1", ip, port);
                CurrentModbusCounter = 0;
                // 使用 MinValue 表示尚未获得任何有效计数边沿时间
                _lastCounterUpdateTime = DateTime.MinValue;
                _lastIntervalMs = 0;
                _smoothedPulseMs = 0;

                while (!token.IsCancellationRequested)
                {
                    var loopStartTicks = Stopwatch.GetTimestamp();
                    try
                    {
                        if (_modbusClient == null || !_modbusClient.Connected)
                        {
                            try
                            {
                                _modbusClient?.Disconnect();
                            }
                            catch
                            {
                                // ignored
                            }

                            _modbusClient = new ModbusClient(ip, port)
                            {
                                ConnectionTimeout = 3000,
                                UnitIdentifier = 1
                            };
                            try
                            {
                                _modbusClient.Connect();
                                Log.Information("Modbus 已连接: {Ip}:{Port}", ip, port);
                            }
                            catch (Exception)
                            {
                                Log.Warning("Modbus 连接失败或超时，3秒后重试");
                                await Task.Delay(3000, token);
                                continue;
                            }
                        }

                        var now = DateTime.Now;
                        int[]? regs;
                        try
                        {
                            regs = _modbusClient.ReadHoldingRegisters(0, 2);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "读取 Modbus 保持寄存器失败，将重连");
                            try
                            {
                                _modbusClient.Disconnect();
                            }
                            catch
                            {
                                // ignored
                            }

                            await Task.Delay(500, token);
                            continue;
                        }

                        if (regs.Length == 2)
                        {
                            // 0 为高 16 位，1 为低 16 位
                            uint hi = (ushort)regs[0];
                            uint lo = (ushort)regs[1];
                            var combined = (hi << 16) | lo;

                            if (_lastCounterUpdateTime == DateTime.MinValue)
                            {
                                CurrentModbusCounter = combined;
                                _lastCounterUpdateTime = now;
                                Log.Information("Modbus首次读取: 值={Value} (HI={Hi}, LO={Lo}) 时间={Time}", combined, hi, lo,
                                    now.ToString("O"));
                                // 为待定包裹设置目标
                                if (!_packagePendingDistances.IsEmpty)
                                    foreach (var kv in _packagePendingDistances.ToArray())
                                    {
                                        var target = unchecked(CurrentModbusCounter + kv.Value);
                                        _packageTargetCounters[kv.Key] = target;
                                        _packagePendingDistances.TryRemove(kv.Key, out _);
                                        Log.Information(
                                            "包裹 {Barcode} 首次目标计数: Target={Target}, Current={Current}, 距离(脉冲)={Distance}",
                                            kv.Key.Barcode, target, CurrentModbusCounter, kv.Value);
                                    }
                            }
                            else if (combined != CurrentModbusCounter)
                            {
                                var delta = combined >= CurrentModbusCounter
                                    ? combined - CurrentModbusCounter
                                    : (ulong)uint.MaxValue - CurrentModbusCounter + 1UL + combined;

                                var span = now - _lastCounterUpdateTime;
                                var perPulseMs = delta > 0 ? span.TotalMilliseconds / delta : 0;
                                if (perPulseMs > 0 && perPulseMs < Settings.DefaultInterval * 5)
                                {
                                    _lastIntervalMs = perPulseMs;
                                    // 指数平滑
                                    _smoothedPulseMs = _smoothedPulseMs <= 0
                                        ? perPulseMs
                                        : PulseEmaAlpha * perPulseMs + (1 - PulseEmaAlpha) * _smoothedPulseMs;
                                }

                                Log.Information(
                                    "Modbus变化: {Prev}->{Curr} Δ={Delta} 窗口={SpanMs:F2}ms perPulseMs={Per:F2}ms smoothed={Smoothed:F2}ms @ {Time}",
                                    CurrentModbusCounter, combined, delta, span.TotalMilliseconds, perPulseMs,
                                    _smoothedPulseMs, now.ToString("O"));
                                // 基于计数过线触发分拣（使用平滑后的脉冲间隔）
                                var effectivePulseMs = _smoothedPulseMs > 0 ? _smoothedPulseMs : perPulseMs;
                                CheckAndTriggerByCounter(CurrentModbusCounter, combined, _lastCounterUpdateTime,
                                    effectivePulseMs);

                                CurrentModbusCounter = combined;
                                _lastCounterUpdateTime = now;

                                // 记录边沿历史（仅在发生变化时）
                                _counterHistory.Enqueue((combined, now));
                                while (_counterHistory.Count > CounterHistoryCapacity &&
                                       _counterHistory.TryDequeue(out _))
                                {
                                }
                            }
                        }

                        // 维持 ~10ms 轮询周期（扣除本轮耗时）
                        var elapsedMs =
                            (int)((Stopwatch.GetTimestamp() - loopStartTicks) * 1000.0 / Stopwatch.Frequency);
                        var sleepMs = 10 - elapsedMs;
                        if (sleepMs > 0) await Task.Delay(sleepMs, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Modbus 轮询异常，1秒后重试");
                        await Task.Delay(1000, token);
                    }
                }

                Log.Information("Modbus 轮询结束");
            }, _modbusPollingCts.Token);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动 Modbus 轮询时发生异常");
        }
    }

    private void StopModbusPolling()
    {
        try
        {
            _modbusPollingCts?.Cancel();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止 Modbus 轮询时发生异常");
        }
        finally
        {
            try
            {
                _modbusClient?.Disconnect();
            }
            catch
            {
                // ignore
            }

            _modbusClient = null;
            _modbusPollingCts?.Dispose();
            _modbusPollingCts = null;
        }
    }
}