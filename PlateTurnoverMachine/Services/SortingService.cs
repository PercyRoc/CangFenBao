using System.Collections.Concurrent;
using System.Text;
using Common.Models.Package;
using Common.Services.Settings;
using PlateTurnoverMachine.Models;
using Serilog;

namespace PlateTurnoverMachine.Services;

/// <summary>
///     分拣服务
/// </summary>
public class SortingService : IDisposable
{
    private readonly object _countLock = new();
    private readonly ConcurrentQueue<PackageInfo> _packageQueue = new();
    private readonly ISettingsService _settingsService;
    private readonly Dictionary<string, TcpConnectionConfig> _tcpConfigs = [];
    private readonly ITcpConnectionService _tcpConnectionService;
    private double _lastIntervalMs;
    private bool _disposed;
    private DateTime _lastTriggerTime;
    private PlateTurnoverSettings? _cachedSettings;
    private readonly IZtoSortingService _ztoSortingService;
    private bool _lastSignalWasLow; // 新增：标记上一个信号是否为低电平
    private DateTime _lastLowSignalTime; // 新增：记录上一次低电平信号的时间

    public SortingService(ITcpConnectionService tcpConnectionService, ISettingsService settingsService, IZtoSortingService ztoSortingService)
    {
        _tcpConnectionService = tcpConnectionService;
        _settingsService = settingsService;
        _ztoSortingService = ztoSortingService;
        _tcpConnectionService.TriggerPhotoelectricDataReceived += OnTriggerPhotoelectricDataReceived;
        _lastTriggerTime = DateTime.Now;

        // 初始化所有TCP配置
        foreach (var item in Settings.Items.Where(static item => !string.IsNullOrEmpty(item.TcpAddress)))
            if (item.TcpAddress != null)
            {
                _tcpConfigs[item.TcpAddress] = new TcpConnectionConfig(item.TcpAddress, 2000);
            }

        // 订阅配置变更事件
        Settings.SettingsChanged += OnSettingsChanged;
    }

    private PlateTurnoverSettings Settings
    {
        get { return _cachedSettings ??= _settingsService.LoadSettings<PlateTurnoverSettings>(); }
    }

    /// <summary>
    ///     获取最近一次计算的高电平信号时间间隔（毫秒）
    /// </summary>
    private double LastIntervalMs =>
        // 如果 _lastIntervalMs 尚未被有效计算（仍为0或初始值），则使用默认间隔
        // 增加一个检查确保返回的是合理的值
        _lastIntervalMs > 0 ? _lastIntervalMs : Settings.DefaultInterval;

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
                Log.Information("包裹 {Barcode} 需要触发 {Distance} 次光电信号", package.Barcode, turnoverItem.Distance);
            }

            // 计算初始 PackageCount
            var timeSinceCreation = DateTime.Now - package.TriggerTimestamp;
            var interval = LastIntervalMs > 0 ? LastIntervalMs : Settings.DefaultInterval;
            int initialCount = 0;
            if (interval > 0)
            {
                initialCount = (int)Math.Floor(timeSinceCreation.TotalMilliseconds / interval);
                Log.Debug("包裹 {Barcode} 创建后已过去 {ElapsedMs:F2} ms，估算初始光电计数为 {InitialCount} (间隔: {IntervalMs:F2} ms)",
                    package.Barcode, timeSinceCreation.TotalMilliseconds, initialCount, interval);
            }
            else
            {
                Log.Warning("无法计算包裹 {Barcode} 的初始光电计数，因为光电触发间隔无效 ({Interval}ms)", package.Barcode, interval);
            }
            package.PackageCount = initialCount; // 使用计算出的初始值

            package.SetStatus(PackageStatus.SortSuccess);
            _packageQueue.Enqueue(package);
            Log.Information("包裹 {Barcode} 已添加到分拣队列，目标格口：{ChuteNumber}, 初始计数: {InitialCount}", package.Barcode, package.ChuteNumber, package.PackageCount);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "添加包裹到分拣队列时发生错误：{Barcode}", package.Barcode);
            throw;
        }
    }

    private void OnTriggerPhotoelectricDataReceived(object? sender, TcpDataReceivedEventArgs e)
    {
        // 添加详细日志，显示收到的原始数据
        try
        {
            var rawData = e.Data.Length > 0 
                ? Encoding.ASCII.GetString(e.Data) 
                : "<空数据>";
            Log.Information("收到触发光电原始数据: {RawData}", rawData);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "尝试解析触发光电原始数据时出错");
        }
        
        ProcessSignalInternal(e.Data, e.ReceivedTime);
    }

    /// <summary>
    /// 内部处理信号的核心逻辑
    /// </summary>
    private void ProcessSignalInternal(byte[]? data, DateTime triggerTime)
    {
        try
        {
            // 检查数据是否为空
            if (data == null || data.Length == 0)
            {
                Log.Warning("收到空的触发光电数据");
                _lastSignalWasLow = false; // 重置状态
                return;
            }

            // 解析消息
            var message = Encoding.ASCII.GetString(data);
            Log.Information("解析触发光电数据: {Message} at {Timestamp}", message, triggerTime.ToString("O"));

            bool isHighSignal = message.Contains("+OCCH1:1");
            bool isLowSignal = message.Contains("+OCCH1:0");

            if (isHighSignal)
            {
                Log.Debug("检测到高电平信号");
                var intervalMs = (triggerTime - _lastTriggerTime).TotalMilliseconds;
                Log.Debug("高电平信号时间间隔：{Interval:F2}毫秒 (Raw)", intervalMs);

                // 只有当间隔看起来合理时才更新 (避免初始启动或长时间无信号导致异常间隔)
                if (intervalMs > 0 && intervalMs < (Settings.DefaultInterval * 5)) // 设定一个合理上限，例如默认间隔的5倍
                {
                    _lastIntervalMs = intervalMs;
                    Log.Debug("使用高电平信号更新 LastIntervalMs: {Interval:F2}ms", _lastIntervalMs);
                }
                else
                {
                    Log.Warning("高电平信号间隔 {IntervalMs:F2}ms 不在合理范围 (0, {MaxInterval}ms)，未更新 LastIntervalMs", intervalMs, Settings.DefaultInterval * 5);
                }

                _lastTriggerTime = triggerTime;
                _lastSignalWasLow = false;

                // 处理计数增加和距离检查
                ProcessPackageCountingAndTriggering(triggerTime, "Real High Signal");
            }
            else if (isLowSignal)
            {
                Log.Debug("检测到低电平信号");
                if (_lastSignalWasLow)
                {
                    // 连续第二次收到低电平，触发补偿逻辑
                    Log.Warning("连续收到两次低电平信号，可能丢失高电平，触发补偿逻辑");
                    var intervalMs = (triggerTime - _lastLowSignalTime).TotalMilliseconds;
                    Log.Debug("两次低电平信号时间间隔：{Interval:F2}毫秒", intervalMs);

                    // 更新 LastIntervalMs (使用两次低电平的间隔)
                     if (intervalMs > 0 && intervalMs < (Settings.DefaultInterval * 5)) // 设定一个合理上限
                     {
                         _lastIntervalMs = intervalMs;
                         Log.Debug("使用补偿逻辑更新 LastIntervalMs: {Interval:F2}ms", _lastIntervalMs);
                     }
                     else
                     {
                        Log.Warning("补偿逻辑计算的间隔 {IntervalMs:F2}ms 不在合理范围 (0, {MaxInterval}ms)，未更新 LastIntervalMs", intervalMs, Settings.DefaultInterval * 5);
                     }


                    // 估算丢失的高电平时间 (中点)
                    var estimatedHighTime = _lastLowSignalTime + TimeSpan.FromMilliseconds(intervalMs / 2);
                    Log.Debug("估算丢失的高电平时间: {EstimatedTime}", estimatedHighTime.ToString("O"));
                    _lastTriggerTime = estimatedHighTime; // 更新最后触发时间为估算时间

                    _lastSignalWasLow = false; // 重置标志

                    // 处理补偿计数增加和距离检查
                    ProcessPackageCountingAndTriggering(estimatedHighTime, "Compensated Low Signal");
                }
                else
                {
                    // 第一次收到低电平
                    _lastSignalWasLow = true;
                    _lastLowSignalTime = triggerTime;
                    Log.Debug("记录第一次低电平信号时间: {LowTime}", _lastLowSignalTime.ToString("O"));
                }
            }
            else
            {
                Log.Warning("收到无法识别的光电信号: {Message}", message);
                _lastSignalWasLow = false; // 重置状态
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理光电信号内部逻辑时发生错误");
            _lastSignalWasLow = false; // 发生错误时重置状态
        }
    }

    /// <summary>
    /// 处理包裹计数增加和检查触发距离的通用逻辑
    /// </summary>
    /// <param name="signalTime">信号触发时间（真实或估算）</param>
    /// <param name="triggerSource">触发源描述 (用于日志)</param>
    private void ProcessPackageCountingAndTriggering(DateTime signalTime, string triggerSource)
    {
         Log.Debug("开始更新包裹计数 (触发源: {Source})...", triggerSource);
         var totalPackages = _packageQueue.Count;
         var updatedPackages = 0;
         foreach (var package in _packageQueue)
         {
             package.PackageCount++;
             package.SetTriggerTimestamp(signalTime); // 同时更新最后触发时间
             Log.Debug("包裹 {Barcode} 计数增加为: {Count}", package.Barcode, package.PackageCount);
             updatedPackages++;
         }
         Log.Debug("包裹计数更新完毕 (更新了 {UpdatedCount}/{TotalCount} 个)", updatedPackages, totalPackages);

         // 遍历队列判断是否达到距离 (此部分逻辑不变)
         Log.Debug("开始检查队列中包裹是否到达目标距离 (触发源: {Source})...", triggerSource);
         var checkedPackages = 0;
         var packagesToDequeue = new List<PackageInfo>(); // 存储需要移除的包裹

         foreach (var package in _packageQueue)
         {
             checkedPackages++;
             // 获取包裹对应的翻板机配置
             var turnoverItem = Settings.Items.FirstOrDefault(item => item.MappingChute == package.ChuteNumber);
             if (turnoverItem == null) continue;

             Log.Debug("检查包裹 {Barcode} 目标距离: {Distance}, 当前计数: {Count}", package.Barcode, turnoverItem.Distance, package.PackageCount);

             if (!(package.PackageCount >= turnoverItem.Distance)) continue;

             Log.Information("包裹 {Barcode} 满足距离条件 ({Count} >= {Distance}) (触发源: {Source})，准备触发落格",
                 package.Barcode, package.PackageCount, turnoverItem.Distance, triggerSource);

             Log.Information("包裹 {Barcode} 已到达目标位置，触发次数：{Count}，目标格口：{ChuteNumber}，总耗时：{TotalTime:F2}毫秒",
                 package.Barcode, package.PackageCount, package.ChuteNumber,
                 (signalTime - package.CreateTime).TotalMilliseconds);

             // 计算延迟时间，使用 LastIntervalMs
             var delayMs = (int)(LastIntervalMs * turnoverItem.DelayFactor);
             Log.Debug("包裹 {Barcode} 将在 {Delay} 毫秒后触发落格，使用最近间隔：{Interval:F2}毫秒，延迟系数：{Factor:F2}",
                 package.Barcode, delayMs, LastIntervalMs, turnoverItem.DelayFactor);

             // 异步发送落格命令 (将包裹实例传递给异步方法)
             var packageToSend = package; // 捕获当前循环的包裹实例
             packagesToDequeue.Add(packageToSend); // 标记此包裹需要被移除

             _ = Task.Run(async () =>
             {
                 try
                 {
                     // 1. 检查配置的 TCP 地址
                     if (string.IsNullOrEmpty(turnoverItem.TcpAddress))
                     {
                         Log.Error("包裹 {Barcode} 的翻板机未配置TCP地址", packageToSend.Barcode);
                         return;
                     }

                     // 2. 从字典中查找对应的 TcpConnectionConfig
                     if (!_tcpConfigs.TryGetValue(turnoverItem.TcpAddress, out var targetConfig))
                     {
                          Log.Error("包裹 {Barcode} 配置的TCP地址 {TcpAddress} 在配置字典中未找到", packageToSend.Barcode, turnoverItem.TcpAddress);
                          return;
                     }

                     // 准备落格命令
                     var outNumber = turnoverItem.IoPoint!.ToUpper().Replace("OUT", "");
                     var lockCommand = $"AT+STACH{outNumber}=1\r\n"; // 直接使用 \r\n
                     var commandData = Encoding.ASCII.GetBytes(lockCommand);

                     // === 添加日志：确认发送目标和命令 ===
                     Log.Information("准备向TCP模块 {TargetIp} 发送落格命令 {Command} (包裹 {Barcode})",
                         targetConfig.IpAddress, lockCommand.TrimEnd('\r', '\n'), packageToSend.Barcode); // 日志中移除换行符

                     // 3. 发送落格命令到目标配置
                     await _tcpConnectionService.SendToTcpModuleAsync(targetConfig, commandData);
                     Log.Information("包裹 {Barcode} 已发送落格命令：{Command} 到 {TargetIp}", packageToSend.Barcode, lockCommand.TrimEnd('\r', '\n'), targetConfig.IpAddress);

                     // 等待磁铁吸合时间后复位
                     await Task.Delay(turnoverItem.MagnetTime);
                     var resetCommand = $"AT+STACH{outNumber}=0\r\n"; // 直接使用 \r\n
                     var resetData = Encoding.ASCII.GetBytes(resetCommand);

                     // === 添加日志：确认发送目标和命令 ===
                     Log.Information("准备向TCP模块 {TargetIp} 发送复位命令 {Command} (包裹 {Barcode})",
                         targetConfig.IpAddress, resetCommand.TrimEnd('\r', '\n'), packageToSend.Barcode); // 日志中移除换行符

                     // 4. 发送复位命令到目标配置
                     await _tcpConnectionService.SendToTcpModuleAsync(targetConfig, resetData);
                     Log.Debug("包裹 {Barcode} 已发送复位命令：{Command} 到 {TargetIp}", packageToSend.Barcode, resetCommand.TrimEnd('\r', '\n'), targetConfig.IpAddress);

                     _ = ReportSortingResultAsync(packageToSend);
                 }
                 catch (Exception ex)
                 {
                     Log.Error(ex, "包裹 {Barcode} 发送落格命令时发生错误", packageToSend.Barcode);
                 }
             });
         }

         // 安全地移除已处理的包裹
         if (packagesToDequeue.Count > 0)
         {
             Log.Debug("准备从队列中移除 {Count} 个已处理的包裹...", packagesToDequeue.Count);
             // 由于 ConcurrentQueue 不支持直接按值移除，我们需要重建队列或采用过滤出队的方式
             // 这里采用更健壮的方式：创建一个新的队列，只包含未处理的包裹
             var remainingPackages = _packageQueue.Where(p => !packagesToDequeue.Contains(p));
             var newQueue = new ConcurrentQueue<PackageInfo>(remainingPackages);

             // 替换旧队列 (这里需要考虑线程安全，但对于单线程处理信号的场景，这样是可接受的)
             // 或者，如果 _packageQueue 的修改总是在这个方法内，可以直接操作
             // 为了简单起见，假设信号处理是串行的
             while (_packageQueue.TryDequeue(out _)) { } // 清空旧队列
             foreach(var p in newQueue) { _packageQueue.Enqueue(p); } // 填入新队列

             foreach(var dequeuedPackage in packagesToDequeue)
             {
                 Log.Information("包裹 {Barcode} 已处理并从逻辑队列中移除", dequeuedPackage.Barcode);
             }
             Log.Debug("队列移除操作完成，当前队列大小: {Size}", _packageQueue.Count);
         }


         Log.Debug("检查了 {CheckedCount} 个包裹是否到达距离 (触发源: {Source})", checkedPackages, triggerSource);
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        // 清除缓存的设置
        _cachedSettings = null;

        // 获取新的配置
        var newSettings = _settingsService.LoadSettings<PlateTurnoverSettings>();

        // 检查TCP地址和端口号是否发生变化
        var changedConfigs = new List<(string IpAddress, TcpConnectionConfig Config)>();
        foreach (var item in newSettings.Items.Where(static item => !string.IsNullOrEmpty(item.TcpAddress)))
        {
            if (item.TcpAddress == null) continue;

            // 检查是否已存在该TCP地址的配置
            if (!_tcpConfigs.ContainsKey(item.TcpAddress))
            {
                // 添加新的配置
                changedConfigs.Add((item.TcpAddress, new TcpConnectionConfig(item.TcpAddress, 2000)));
            }
        }

        // 检查是否有需要移除的配置
        var removedConfigs = _tcpConfigs.Keys
            .Where(ip => newSettings.Items.All(item => item.TcpAddress != ip))
            .ToList();

        // 更新配置
        foreach (var (ipAddress, config) in changedConfigs)
        {
            _tcpConfigs[ipAddress] = config;
            Log.Information("更新TCP配置：{Address}", ipAddress);
        }

        // 移除不再使用的配置
        foreach (var ipAddress in removedConfigs)
        {
            _tcpConfigs.Remove(ipAddress);
            Log.Information("移除TCP配置：{Address}", ipAddress);
        }

        // 如果有配置变更，记录日志
        if (changedConfigs.Count > 0 || removedConfigs.Count > 0)
        {
            Log.Information("翻板机配置已更新，变更：{ChangedCount}个，移除：{RemovedCount}个",
                changedConfigs.Count, removedConfigs.Count);
        }
    }

    private async Task ReportSortingResultAsync(PackageInfo package)
    {
        try
        {
            if (string.IsNullOrEmpty(Settings.ZtoPipelineCode))
            {
                return;
            }

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

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            _tcpConnectionService.TriggerPhotoelectricDataReceived -= OnTriggerPhotoelectricDataReceived;
            Settings.SettingsChanged -= OnSettingsChanged;
        }

        _disposed = true;
    }
}