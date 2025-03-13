using System.Collections.Concurrent;
using System.Text;
using Common.Models.Package;
using Common.Services.Settings;
using Presentation_PlateTurnoverMachine.Models;
using Serilog;

namespace Presentation_PlateTurnoverMachine.Services;

/// <summary>
///     分拣服务
/// </summary>
public class SortingService : IDisposable
{
    private const int MaxIntervalCount = 100;
    private readonly Dictionary<(string IpAddress, int Channel), bool> _channelStates = new();
    private readonly object _channelStatesLock = new();
    private readonly object _countLock = new();
    private readonly object _intervalsLock = new();
    private readonly ConcurrentQueue<PackageInfo> _packageQueue = new();
    private readonly ISettingsService _settingsService;
    private readonly Dictionary<string, TcpConnectionConfig> _tcpConfigs = new();
    private readonly ITcpConnectionService _tcpConnectionService;
    private readonly Queue<double> _triggerIntervals = new();
    private int _currentCount;
    private bool _disposed;
    private DateTime _lastTriggerTime;

    public SortingService(ITcpConnectionService tcpConnectionService, ISettingsService settingsService)
    {
        _tcpConnectionService = tcpConnectionService;
        _settingsService = settingsService;
        _tcpConnectionService.TriggerPhotoelectricDataReceived += OnTriggerPhotoelectricDataReceived;
        _tcpConnectionService.TcpModuleDataReceived += OnTcpModuleDataReceived;
        _lastTriggerTime = DateTime.Now;

        // 初始化所有TCP配置
        foreach (var item in Settings.Items)
            if (!string.IsNullOrEmpty(item.TcpAddress))
                _tcpConfigs[item.TcpAddress] = new TcpConnectionConfig(item.TcpAddress, 2000);
    }

    private PlateTurnoverSettings Settings => _settingsService.LoadSettings<PlateTurnoverSettings>();

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
    public void EnqueuePackage(PackageInfo package)
    {
        try
        {
            // 获取包裹对应的翻板机配置
            var turnoverItem = Settings.Items.FirstOrDefault(item => item.MappingChute == package.ChuteName);
            if (turnoverItem == null)
            {
                Log.Warning("包裹 {Barcode} 的目标格口 {ChuteName} 未找到对应的翻板机配置", package.Barcode, package.ChuteName);
                // 如果找不到对应的翻板机配置，分配到异常格口
                package.ChuteName = Settings.ErrorChute;
            }
            else
            {
                // 检查目标格口是否锁定
                var outNumber = int.Parse(turnoverItem.IoPoint!.ToUpper().Replace("OUT", ""));
                var isLocked = IsChannelLocked(turnoverItem.TcpAddress!, outNumber);
                if (isLocked)
                {
                    Log.Warning("包裹 {Barcode} 的目标格口 {ChuteName} 已锁定，重新分配到异常格口", package.Barcode, package.ChuteName);
                    package.ChuteName = Settings.ErrorChute;
                }
                else
                {
                    Log.Information("包裹 {Barcode} 需要触发 {Distance} 次光电信号", package.Barcode, turnoverItem.Distance);
                }
            }

            _packageQueue.Enqueue(package);
            Log.Information("包裹 {Barcode} 已添加到分拣队列，目标格口：{ChuteName}", package.Barcode, package.ChuteName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "添加包裹到分拣队列时发生错误：{Barcode}", package.Barcode);
            throw;
        }
    }

    private bool IsChannelLocked(string ipAddress, int channel)
    {
        lock (_channelStatesLock)
        {
            return _channelStates.TryGetValue((ipAddress, channel), out var isLocked) && isLocked;
        }
    }

    private void OnTriggerPhotoelectricDataReceived(object? sender, TcpDataReceivedEventArgs e)
    {
        ProcessTriggerSignal(e.Data, e.ReceivedTime);
    }

    private void OnTcpModuleDataReceived(object? sender, TcpModuleDataReceivedEventArgs e)
    {
        try
        {
            var message = Encoding.ASCII.GetString(e.Data);

            // 处理OCCH_ALL消息
            if (!message.StartsWith("+OCCH_ALL:")) return;

            var channelStates = message.Replace("+OCCH_ALL:", "").Split(',');
            if (channelStates.Length != 8)
            {
                Log.Warning("收到无效的通道状态数据：{Message}", message);
                return;
            }

            // 获取对应的翻板机配置
            var turnoverItem = Settings.Items.FirstOrDefault(item => item.TcpAddress == e.Config.IpAddress);
            if (turnoverItem == null)
            {
                Log.Warning("未找到TCP地址 {Address} 对应的翻板机配置", e.Config.IpAddress);
                return;
            }

            // 更新通道状态
            lock (_channelStatesLock)
            {
                for (var i = 0; i < channelStates.Length; i++)
                {
                    var isActive = channelStates[i] == "1";
                    _channelStates[(e.Config.IpAddress, i + 1)] = isActive;
                    Log.Debug("TCP模块 {Address} 通道 {Channel} {Status}",
                        e.Config.IpAddress, i + 1, isActive ? "已锁定" : "已解锁");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理TCP模块数据时发生错误");
        }
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
            // 检查是否是触发光电+OCCH1:1信号
            if (!IsValidTriggerSignal(data)) return;

            lock (_countLock)
            {
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
                    var turnoverItem = Settings.Items.FirstOrDefault(item => item.MappingChute == package.ChuteName);
                    if (turnoverItem == null) continue;

                    // 如果当前计数等于配置的距离，说明包裹到达了目标位置
                    if (!(_currentCount >= turnoverItem.Distance)) continue;
                    Log.Information("包裹 {Barcode} 已到达目标位置，触发次数：{Count}，目标格口：{ChuteName}，总耗时：{TotalTime:F2}毫秒",
                        package.Barcode, _currentCount, package.ChuteName,
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
        catch (Exception ex)
        {
            Log.Error(ex, "处理触发光电信号时发生错误");
        }
    }

    private static bool IsValidTriggerSignal(byte[] data)
    {
        try
        {
            var message = Encoding.ASCII.GetString(data);
            return message.Contains("+OCCH1:1");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "验证触发信号时发生错误");
            return false;
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            _tcpConnectionService.TriggerPhotoelectricDataReceived -= OnTriggerPhotoelectricDataReceived;
            _tcpConnectionService.TcpModuleDataReceived -= OnTcpModuleDataReceived;
        }

        _disposed = true;
    }
}