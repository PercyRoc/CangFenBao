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
    private const int MaxIntervalCount = 100;
    private readonly object _countLock = new();
    private readonly object _intervalsLock = new();
    private readonly ConcurrentQueue<PackageInfo> _packageQueue = new();
    private readonly ISettingsService _settingsService;
    private readonly Dictionary<string, TcpConnectionConfig> _tcpConfigs = [];
    private readonly ITcpConnectionService _tcpConnectionService;
    private readonly Queue<double> _triggerIntervals = new();
    private int _currentCount;
    private bool _disposed;
    private DateTime _lastTriggerTime;
    private PlateTurnoverSettings? _cachedSettings;
    private readonly IZtoSortingService _ztoSortingService;
    private bool _lastSignalWasLow; // 记录上一次信号是否为低电平

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
    ///     获取最近100次触发的平均时间间隔（毫秒）
    /// </summary>
    private double AverageInterval
    {
        get
        {
            lock (_intervalsLock)
            {
                // 如果队列为空或只有一个数据，使用默认间隔
                return _triggerIntervals.Count <= 1 ? Settings.DefaultInterval : _triggerIntervals.Average();
            }
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     添加包裹到分拣队列
    /// </summary>
    /// <param name="package">包裹信息</param>
    internal async Task EnqueuePackage(PackageInfo package)
    {
        try
        {
            // 先验证面单规则
            if (_ztoSortingService != null && !string.IsNullOrEmpty(package.Barcode))
            {
                // 获取中通面单规则
                var billRule = await _ztoSortingService.GetBillRuleAsync();

                // 检查是否符合面单规则
                if (!string.IsNullOrEmpty(billRule.Pattern) &&
                    !System.Text.RegularExpressions.Regex.IsMatch(package.Barcode, billRule.Pattern))
                {
                    Log.Warning("包裹条码 {Barcode} 不符合中通面单规则，分配到异常口", package.Barcode);
                    package.SetChute(Settings.ErrorChute);
                    package.SetError("不符合中通面单规则");

                    // 推送分拣结果（异常件）
                    _ = ReportSortingResultAsync(package);
                    return;
                }
            }

            // 先调用中通接口获取分拣格口信息
            if (_ztoSortingService != null && !string.IsNullOrEmpty(Settings.ZtoPipelineCode) &&
                !string.IsNullOrEmpty(package.Barcode))
            {
                var sortingInfo = await _ztoSortingService.GetSortingInfoAsync(
                    package.Barcode,
                    Settings.ZtoPipelineCode,
                    _currentCount,
                    Settings.ZtoTrayCode,
                    (float)package.Weight);

                if (sortingInfo != null && sortingInfo.SortPortCode.Count > 0)
                {
                    // 解析服务器返回的格口号
                    if (int.TryParse(sortingInfo.SortPortCode[0], out var chuteNumber))
                    {
                        package.SetChute(chuteNumber);
                        Log.Information("从中通服务器获取到格口号：{ChuteNumber}，包裹：{Barcode}", chuteNumber, package.Barcode);
                    }
                }
            }

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
            package.ProcessingTime = (DateTime.Now - package.TriggerTimestamp).TotalMilliseconds;
            // 检查包裹处理时间与平均光电间隔的关系
            if (package.ProcessingTime > 0)
            {
                if (package.ProcessingTime > AverageInterval * 2)
                {
                    // 处理时间大于两倍平均间隔，计数初始化为2
                    package.PackageCount = 2;
                    Log.Information("包裹 {Barcode} 处理时间 {ProcessingTime:F2}ms 大于两倍平均间隔 {AverageInterval:F2}ms，初始化计数为2", 
                        package.Barcode, package.ProcessingTime, AverageInterval * 2);
                }
                else if (package.ProcessingTime > AverageInterval)
                {
                    // 处理时间大于平均间隔，计数初始化为1
                    package.PackageCount = 1;
                    Log.Information("包裹 {Barcode} 处理时间 {ProcessingTime:F2}ms 大于平均间隔 {AverageInterval:F2}ms，初始化计数为1", 
                        package.Barcode, package.ProcessingTime, AverageInterval);
                }
            }

            _packageQueue.Enqueue(package);
            Log.Information("包裹 {Barcode} 已添加到分拣队列，目标格口：{ChuteNumber}", package.Barcode, package.ChuteNumber);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "添加包裹到分拣队列时发生错误：{Barcode}", package.Barcode);
            throw;
        }
    }

    private void OnTriggerPhotoelectricDataReceived(object? sender, TcpDataReceivedEventArgs e)
    {
        ProcessTriggerSignal(e.Data, e.ReceivedTime);
    }

    /// <summary>
    ///     处理触发光电信号
    /// </summary>
    /// <param name="data">触发光电数据</param>
    /// <param name="triggerTime">触发时间</param>
    private void ProcessTriggerSignal(byte[] data, DateTime triggerTime)
    {
        try
        {
            // 检查是否是触发光电信号
            var message = Encoding.ASCII.GetString(data);
            var isLowSignal = message.Contains("+OCCH1:0");
            var isHighSignal = message.Contains("+OCCH1:1");
            
            // 如果既不是高电平也不是低电平信号，直接返回
            if (!isLowSignal && !isHighSignal) return;
            
            // 检查是否连续收到两次低电平信号
            var needCompensation = isLowSignal && _lastSignalWasLow;
            
            // 更新上一次信号状态
            _lastSignalWasLow = isLowSignal;
            
            // 如果是高电平信号或需要补偿，则继续处理
            if (isHighSignal || needCompensation)
            {
                lock (_countLock)
                {
                    // 如果是连续两次低电平，记录日志
                    if (needCompensation)
                    {
                        Log.Warning("检测到连续两次低电平信号，补偿计数加1");
                    }
                    
                    // 计算时间间隔
                    var interval = triggerTime - _lastTriggerTime;
                    var intervalMs = interval.TotalMilliseconds;

                    // 记录时间间隔
                    lock (_intervalsLock)
                    {
                        _triggerIntervals.Enqueue(intervalMs);
                        if (_triggerIntervals.Count > MaxIntervalCount) _triggerIntervals.Dequeue();
                    }

                    Log.Debug("触发光电信号时间间隔：{Interval:F2}毫秒，最近{Count}次平均间隔：{Average:F2}毫秒",
                        intervalMs, _triggerIntervals.Count, AverageInterval);
                    _lastTriggerTime = triggerTime;

                    _currentCount++;
                    Log.Debug("收到触发信号，当前计数：{Count}", _currentCount);

                    // 更新队列中所有包裹的计数
                    foreach (var package in _packageQueue)
                    {
                        package.PackageCount = _currentCount;
                        package.SetTriggerTimestamp(triggerTime);

                        // 获取包裹对应的翻板机配置
                        var turnoverItem = Settings.Items.FirstOrDefault(item => item.MappingChute == package.ChuteNumber);
                        if (turnoverItem == null) continue;

                        // 如果当前计数等于配置的距离，说明包裹到达了目标位置
                        if (!(_currentCount >= turnoverItem.Distance)) continue;

                        Log.Information("包裹 {Barcode} 已到达目标位置，触发次数：{Count}，目标格口：{ChuteNumber}，总耗时：{TotalTime:F2}毫秒",
                            package.Barcode, _currentCount, package.ChuteNumber,
                            (triggerTime - package.CreateTime).TotalMilliseconds);

                        // 计算延迟时间
                        var delayMs = (int)(AverageInterval * turnoverItem.DelayFactor);
                        Log.Debug("包裹 {Barcode} 将在 {Delay} 毫秒后触发落格，平均间隔：{Average:F2}毫秒，延迟系数：{Factor:F2}",
                            package.Barcode, delayMs, AverageInterval, turnoverItem.DelayFactor);

                        // 异步发送落格命令
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                if (string.IsNullOrEmpty(turnoverItem.TcpAddress))
                                {
                                    Log.Error("包裹 {Barcode} 的翻板机未配置TCP地址", package.Barcode);
                                    return;
                                }

                                // 等待指定的延迟时间
                                await Task.Delay(delayMs);

                                // 准备落格命令
                                var outNumber = turnoverItem.IoPoint!.ToUpper().Replace("OUT", "");
                                var lockCommand = $"AT+STACH{outNumber}=1";
                                var commandData = Encoding.ASCII.GetBytes(lockCommand);

                                // 发送落格命令
                                await _tcpConnectionService.SendToTcpModuleAsync(_tcpConfigs[turnoverItem.TcpAddress],
                                    commandData);
                                Log.Information("包裹 {Barcode} 已发送落格命令：{Command}", package.Barcode, lockCommand);

                                // 等待磁铁吸合时间后复位
                                await Task.Delay(turnoverItem.MagnetTime);
                                var resetCommand = $"AT+STACH{outNumber}=0";
                                var resetData = Encoding.ASCII.GetBytes(resetCommand);
                                await _tcpConnectionService.SendToTcpModuleAsync(_tcpConfigs[turnoverItem.TcpAddress],
                                    resetData);
                                Log.Debug("包裹 {Barcode} 已发送复位命令：{Command}", package.Barcode, resetCommand);

                                _ = ReportSortingResultAsync(package);
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, "包裹 {Barcode} 发送落格命令时发生错误", package.Barcode);
                            }
                        });

                        // 从队列中移除已处理的包裹
                        _packageQueue.TryDequeue(out _);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理触发光电信号时发生错误");
        }
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
            if (_ztoSortingService == null || string.IsNullOrEmpty(Settings.ZtoPipelineCode))
            {
                return;
            }

            // 推送分拣结果
            var result = await _ztoSortingService.ReportSortingResultAsync(
                package,
                Settings.ZtoPipelineCode,
                _currentCount,
                Settings.ZtoTrayCode);

            if (result != null)
            {
                Log.Information("包裹 {Barcode} 分拣结果已上报到中通系统", package.Barcode);
            }
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