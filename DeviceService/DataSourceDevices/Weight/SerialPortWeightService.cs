using System.Globalization;
using System.Text;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.SerialPort; // 引入新服务的命名空间
using Serilog;
using System.Timers;
using Microsoft.VisualBasic;
using Timer = System.Timers.Timer;

// 如果尚未存在，添加LINQ命名空间

namespace DeviceService.DataSourceDevices.Weight;

/// <summary>
///     串口重量称服务 (已重构为使用 SerialPortService)
/// </summary>
public class SerialPortWeightService : IDisposable
{
    // 常量定义
    private const int MaxCacheSize = 100;
    private const int MaxCacheAgeMinutes = 2;
    private const double StableThreshold = 0.01;  // 0.01kg = 10g
    private const int ProcessInterval = 100;

    private readonly object _lock = new();
    private readonly Queue<(double Weight, DateTime Timestamp)> _weightCache = new();
    private readonly AutoResetEvent _weightReceived = new(false);
    private readonly List<double> _weightSamples = [];
    private readonly StringBuilder _receiveBuffer = new();
    private readonly SerialPortService _serialPortService;
    private readonly ISettingsService _settingsService;
    private readonly Timer _statusCheckTimer;

    private bool _disposed;
    private bool _isConnected;
    private DateTime _lastProcessTime = DateTime.MinValue;
    private DateTime _lastDataReceiveTime = DateTime.MinValue;

    public SerialPortWeightService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        var weightSettings = _settingsService.LoadSettings<WeightSettings>();
        _serialPortService = new SerialPortService("WeightScale", weightSettings.SerialPortParams);
        
        // 初始化状态检查定时器，每10秒检查一次
        _statusCheckTimer = new Timer(10000); // 10秒
        _statusCheckTimer.Elapsed += OnStatusCheck;
        _statusCheckTimer.AutoReset = true;
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (_isConnected == value) return;
            _isConnected = value;
            ConnectionChanged?.Invoke("Weight Scale", value);
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public event Action<string, bool>? ConnectionChanged;

    internal bool Start()
    {
        try
        {
            Log.Information("正在启动串口重量称服务 (通过 SerialPortService)...");

            _serialPortService.ConnectionChanged -= HandleConnectionStatusChanged;
            _serialPortService.DataReceived -= HandleDataReceived;
            _serialPortService.ConnectionChanged += HandleConnectionStatusChanged;
            _serialPortService.DataReceived += HandleDataReceived;

            var connectResult = _serialPortService.Connect();
            if (connectResult) 
            {
                _statusCheckTimer.Start(); // 启动状态检查定时器
                return connectResult;
            }
            Log.Error("串口连接失败");
            _serialPortService.ConnectionChanged -= HandleConnectionStatusChanged;
            _serialPortService.DataReceived -= HandleDataReceived;

            return connectResult;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动串口重量称服务时发生异常");
            try
            {
                _serialPortService.ConnectionChanged -= HandleConnectionStatusChanged;
                _serialPortService.DataReceived -= HandleDataReceived;
            }
            catch
            {
                /* 忽略 */
            }

            return false;
        }
    }

    internal void Stop()
    {
        try
        {
            _statusCheckTimer.Stop(); // 停止状态检查定时器
            
            _serialPortService.ConnectionChanged -= HandleConnectionStatusChanged;
            _serialPortService.DataReceived -= HandleDataReceived;

            _serialPortService.Disconnect();

            _receiveBuffer.Clear();
            _weightSamples.Clear();
            lock (_lock)
            {
                _weightCache.Clear();
            }

            Log.Information("串口重量称服务已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止串口重量称服务时发生错误");
        }
    }

    public double? FindNearestWeight(DateTime targetTime)
    {
        // 在方法开始时记录连接状态
        Log.Debug("FindNearestWeight 开始 - 本地状态: {LocalConnected}, 底层状态: {ServiceConnected}, 目标时间: {TargetTime}", 
            IsConnected, _serialPortService.IsConnected, targetTime);
            
        // 主动检查连接状态，如果发现不一致则尝试同步
        if (IsConnected != _serialPortService.IsConnected)
        {
            Log.Warning("发现连接状态不一致，同步状态 - 本地: {Local}, 底层: {Service}", 
                IsConnected, _serialPortService.IsConnected);
            IsConnected = _serialPortService.IsConnected;
        }
        
        // 如果连接断开，尝试重连一次
        if (!_serialPortService.IsConnected)
        {
            Log.Warning("发现串口连接断开，尝试重连...");
            var reconnectResult = _serialPortService.Connect();
            Log.Information("重连结果: {Result}", reconnectResult);
        }
            
        lock (_lock)
        {
            var lowerBound = targetTime.AddMilliseconds(_settingsService.LoadSettings<WeightSettings>().TimeRangeLower);
            var upperBound = targetTime.AddMilliseconds(_settingsService.LoadSettings<WeightSettings>().TimeRangeUpper);

            Log.Debug("查找重量数据 - 目标时间: {TargetTime}, 下限: {LowerBound}, 上限: {UpperBound}, 缓存数量: {CacheCount}",
                targetTime, lowerBound, upperBound, _weightCache.Count);

            (double Weight, DateTime Timestamp)? nearestZeroWeight = null;

            if (_weightCache.Count > 0)
            {
                (double Weight, DateTime Timestamp)? initialWeightInRange = _weightCache
                    .Where(w => w.Timestamp >= lowerBound && w.Timestamp <= upperBound)
                    .OrderBy(w => Math.Abs((w.Timestamp - targetTime).TotalMilliseconds))
                    .FirstOrDefault();

                if (initialWeightInRange != default)
                {
                    var timeDiff = (initialWeightInRange.Value.Timestamp - targetTime).TotalMilliseconds;
                    if (initialWeightInRange.Value.Weight != 0)
                    {
                        Log.Debug("初始查找找到非零重量数据: {Weight:F3}kg, 时间差: {TimeDiff:F0}ms",
                            initialWeightInRange.Value.Weight / 1000, timeDiff);
                        return initialWeightInRange.Value.Weight;
                    }
                    // 找到的是 0，记录下来，继续等待
                    Log.Debug("初始查找找到零重量数据，记录并继续等待: 时间差: {TimeDiff:F0}ms", timeDiff);
                    nearestZeroWeight = initialWeightInRange;
                }
                else
                {
                    // Log the nearest out-of-range weight only if no in-range weight was found yet
                    var nearestWeight = _weightCache
                        .OrderBy(w => Math.Abs((w.Timestamp - targetTime).TotalMilliseconds))
                        .FirstOrDefault();
                    if (nearestWeight != default)
                    {
                        var timeDiff = (nearestWeight.Timestamp - targetTime).TotalMilliseconds;
                        Log.Debug("未找到时间范围内的数据，最近的数据超出范围: {Weight:F3}kg, 时间差: {TimeDiff:F0}ms, 时间戳: {Timestamp}",
                            nearestWeight.Weight / 1000, timeDiff, nearestWeight.Timestamp);
                    }
                }
            }
            else
            {
                Log.Debug("重量缓存为空");
            }

            // 如果初始找到了非零值，上面已经 return 了，能走到这里说明要么没找到，要么找到的是0

            if (!_serialPortService.IsConnected && DateTime.Now > upperBound)
            {
                Log.Debug("串口未连接且当前时间已超过上限，不再等待新数据");
                // 如果之前找到了0，则返回0，否则返回null
                return nearestZeroWeight?.Weight;
            }

            if (DateTime.Now > upperBound)
            {
                Log.Debug("当前时间已超过上限，不再等待新数据");
                // 如果之前找到了0，则返回0，否则返回null
                return nearestZeroWeight?.Weight;
            }

            var waitTime = upperBound - DateTime.Now;
            if (waitTime <= TimeSpan.Zero)
            {
                 // 等待时间已过，如果之前找到了0，则返回0，否则返回null
                 Log.Debug("等待时间已过，返回找到的零重量（如有）或null");
                 return nearestZeroWeight?.Weight;
            }


            Log.Debug("继续等待新的重量数据（或非零重量），最大等待时间: {WaitTime:F0}ms", waitTime.TotalMilliseconds);

            var remainingTime = waitTime;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (remainingTime > TimeSpan.Zero && sw.Elapsed < waitTime)
            {
                var currentWaitTimeout = TimeSpan.FromMilliseconds(Math.Min(100, remainingTime.TotalMilliseconds));

                if (_weightReceived.WaitOne(currentWaitTimeout))
                {
                    Log.Debug("收到新的重量数据信号，重新查找");
                    var weightInRangeDuringWait = _weightCache
                        .Where(w => w.Timestamp >= lowerBound && w.Timestamp <= upperBound)
                        // 在等待期间，我们总是希望找到最新的非零值，或者最接近的零值（如果只有零）
                        .OrderByDescending(w => w.Timestamp) // 优先考虑最新的数据
                        .ThenBy(w => Math.Abs((w.Timestamp - targetTime).TotalMilliseconds))
                        .ToList(); // 获取所有符合条件的，以便区分0和非0

                    var nonZeroWeight = weightInRangeDuringWait.FirstOrDefault(w => w.Weight != 0);

                    if (nonZeroWeight != default)
                    {
                        sw.Stop();
                        var timeDiff = (nonZeroWeight.Timestamp - targetTime).TotalMilliseconds;
                        Log.Debug("等待后找到符合条件的非零重量数据: {Weight:F3}kg, 时间差: {TimeDiff:F0}ms",
                            nonZeroWeight.Weight / 1000, timeDiff);
                        return nonZeroWeight.Weight;
                    }

                    // 检查是否有零重量数据
                    var zeroWeight = weightInRangeDuringWait.FirstOrDefault(w => w.Weight == 0);
                    if (zeroWeight != default)
                    {
                        // 如果当前找到的零重量比之前记录的更接近目标时间，则更新
                        if (nearestZeroWeight == null ||
                            Math.Abs((zeroWeight.Timestamp - targetTime).TotalMilliseconds) <
                            Math.Abs((nearestZeroWeight.Value.Timestamp - targetTime).TotalMilliseconds))
                        {
                             Log.Debug("等待期间找到零重量数据，更新记录并继续等待");
                             nearestZeroWeight = zeroWeight;
                        }
                    }


                    Log.Debug("收到信号但新数据不符合时间范围或仍为零，继续等待");
                }

                if (!_serialPortService.IsConnected)
                {
                    Log.Warning("等待重量数据期间串口连接断开，停止等待 - 本地状态: {LocalConnected}, 底层状态: {ServiceConnected}", 
                        IsConnected, _serialPortService.IsConnected);
                    break; // 退出 while 循环
                }

                remainingTime = upperBound - DateTime.Now;
            }

            sw.Stop();

            // 等待结束（超时或断开连接）
            if (nearestZeroWeight != null)
            {
                Log.Debug("等待结束 ({Elapsed}ms)，未找到非零重量，返回找到的最佳零重量", sw.ElapsedMilliseconds);
                return nearestZeroWeight.Value.Weight; // 返回找到的 0 重量
            }

            Log.Debug("等待结束 ({Elapsed}ms)，未找到任何符合条件的重量数据", sw.ElapsedMilliseconds);
            return null; // 在整个过程中（包括等待）都没有找到任何符合条件的重量
        }
    }

    private void HandleConnectionStatusChanged(bool isConnected)
    {
        Log.Debug("SerialPortWeightService - 收到连接状态变更: {IsConnected}, 当前本地状态: {LocalConnected}, 底层服务状态: {ServiceConnected}", 
            isConnected, IsConnected, _serialPortService.IsConnected);
        
        // 验证状态一致性
        if (isConnected != _serialPortService.IsConnected)
        {
            Log.Warning("连接状态不一致 - 事件参数: {EventConnected}, 底层服务: {ServiceConnected}", 
                isConnected, _serialPortService.IsConnected);
        }
        
        IsConnected = isConnected;
        if (isConnected) return;
        lock (_lock)
        {
            _receiveBuffer.Clear();
            _weightSamples.Clear();
        }

        Log.Debug("串口连接断开，内部缓冲区和样本已清空");
    }

    private void HandleDataReceived(byte[] data)
    {
        if (data.Length == 0) return;

        try
        {
            var receivedString = Encoding.ASCII.GetString(data);
            Log.Information("SerialPortWeightService - 收到原始数据: {Data}, 长度: {Length}, 连接状态: {Connected}, 时间: {Time}", 
                receivedString, data.Length, _serialPortService.IsConnected,DateAndTime.Now);

            // 更新最后接收数据的时间
            _lastDataReceiveTime = DateTime.Now;

            // 如果收到数据但连接状态显示断开，这是异常情况
            if (!_serialPortService.IsConnected)
            {
                Log.Warning("收到数据但连接状态显示断开，这可能是状态同步问题");
            }

            lock (_lock)
            {
                _receiveBuffer.Append(receivedString);
                Log.Debug("当前接收缓冲区内容: {BufferContent}, 长度: {Length}", _receiveBuffer.ToString(), _receiveBuffer.Length);

                const int maxInternalBufferSize = 4096; // 定义一个合适的缓冲区大小
                if (_receiveBuffer.Length > maxInternalBufferSize)
                {
                    Log.Warning("内部接收缓冲区超过限制 ({Length} > {Limit})，清空缓冲区", _receiveBuffer.Length, maxInternalBufferSize);
                    _receiveBuffer.Clear();
                    return;
                }

                ProcessBuffer(DateTime.Now);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理接收到的串口数据时发生错误");
            lock (_lock)
            {
                _receiveBuffer.Clear();
            }
        }
    }

    /// <summary>
    /// 处理内部缓冲区的重量数据 (需要调整以使用 StringBuilder)
    /// </summary>
    private void ProcessBuffer(DateTime receiveTime)
    {
        if ((receiveTime - _lastProcessTime).TotalMilliseconds < ProcessInterval) return;
        _lastProcessTime = receiveTime;

        CleanExpiredWeightData(receiveTime);

        var bufferContent = _receiveBuffer.ToString();
        if (string.IsNullOrEmpty(bufferContent)) return;

        Log.Verbose("开始处理内部缓冲区，内容长度: {Length}", bufferContent.Length);

        var processedLength = 0;

        try
        {
            var lastSeparatorIndex = bufferContent.LastIndexOf('=');

            if (lastSeparatorIndex == -1)
            {
                Log.Verbose("缓冲区中未找到分隔符 '='，等待更多数据");
                if (_receiveBuffer.Length <= 100) return;
                Log.Warning("缓冲区过长但无分隔符，可能数据格式错误，清空缓冲区");
                _receiveBuffer.Clear();

                return;
            }

            var dataToProcess = bufferContent[..lastSeparatorIndex];
            processedLength = lastSeparatorIndex + 1;

            Log.Verbose("准备处理的数据段: {DataSegment}", dataToProcess);

            var dataSegments = dataToProcess.Split(['='], StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            foreach (var segment in dataSegments)
            {
                if (segment.Length < 3)
                {
                    Log.Warning("无效的重量数据段 (太短): \"{Segment}\"", segment);
                    continue;
                }

                var valuePart = segment.Length >= 6 ? segment[..6] : segment;
                var reversedValue = ReverseWeight(valuePart);
                Log.Verbose("解析反转后的值: {ReversedValue} (来自段: {Segment})", reversedValue, valuePart);

                if (float.TryParse(reversedValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var weightInKg))
                {
                    var weightG = weightInKg * 1000;
                    if (_settingsService.LoadSettings<WeightSettings>().WeightType == WeightType.Static)
                    {
                        ProcessStaticWeight(weightG, receiveTime);
                    }
                    else
                    {
                        ProcessDynamicWeight(weightG, receiveTime);
                    }
                }
                else
                {
                    Log.Warning("无法解析反转后的重量数据: {ReversedData} (原始段: {OriginalSegment})", reversedValue, valuePart);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "解析重量数据缓冲区时发生错误");
            _receiveBuffer.Clear();
        }
        finally
        {
            if (processedLength > 0 && processedLength <= _receiveBuffer.Length)
            {
                Log.Verbose("从缓冲区移除已处理的 {ProcessedLength} 个字符", processedLength);
                _receiveBuffer.Remove(0, processedLength);
                Log.Verbose("处理后剩余缓冲区内容: \"{RemainingBuffer}\"", _receiveBuffer.ToString());
            }
            else if (processedLength > _receiveBuffer.Length)
            {
                Log.Warning("计算的处理长度 {ProcessedLength} 大于缓冲区长度 {BufferLength}，清空缓冲区", processedLength,
                    _receiveBuffer.Length);
                _receiveBuffer.Clear();
            }
        }
    }

    private void ProcessStaticWeight(double weightG, DateTime timestamp)
    {
        // 在静态称重模式下，零重量通常表示没有包裹或称重异常，不应参与稳定性判断
        if (weightG <= 0)
        {
            Log.Debug("静态称重模式下忽略零重量或负重量: {Weight:F2}g", weightG);
            return;
        }

        _weightSamples.Add(weightG);
        Log.Debug("添加重量样本: {Weight:F2}g, 当前样本数: {Count}", weightG, _weightSamples.Count);

        while (_weightSamples.Count > _settingsService.LoadSettings<WeightSettings>().StableCheckCount) 
        {
            _weightSamples.RemoveAt(0);
            Log.Debug("移除最旧样本，当前样本数: {Count}", _weightSamples.Count);
        }

        if (_weightSamples.Count < _settingsService.LoadSettings<WeightSettings>().StableCheckCount)
        {
            Log.Debug("样本数量不足，需要 {Required} 个样本，当前: {Current}", 
                _settingsService.LoadSettings<WeightSettings>().StableCheckCount, 
                _weightSamples.Count);
            return;
        }

        var average = _weightSamples.Average();
        const double stableThresholdG = StableThreshold * 1000;
        var isStable = _weightSamples.All(w => Math.Abs(w - average) <= stableThresholdG);

        if (!isStable)
        {
            var samplesString = string.Join(", ", _weightSamples.Select(w => w.ToString("F2")));
            Log.Debug("重量样本不稳定: Avg={Avg:F2}g, 阈值={Threshold:F2}g, Samples=[{Samples}]", 
                average, stableThresholdG, samplesString);
            return;
        }

        Log.Information("重量稳定: {Weight:F2}g, 样本数: {Count}", average, _weightSamples.Count);

        lock (_lock)
        {
            _weightCache.Enqueue((average, timestamp));
            while (_weightCache.Count > MaxCacheSize) _weightCache.Dequeue();
        }

        _weightReceived.Set();
        _weightSamples.Clear();
    }

    private void ProcessDynamicWeight(double weightG, DateTime timestamp)
    {

        lock (_lock)
        {
            _weightCache.Enqueue((weightG, timestamp));
            while (_weightCache.Count > MaxCacheSize) _weightCache.Dequeue();
        }

        _weightReceived.Set();
    }

    private void CleanExpiredWeightData(DateTime currentTime)
    {
        lock (_lock)
        {
            var expireTime = currentTime.AddMinutes(-MaxCacheAgeMinutes);
            var removedCount = 0;
            while (_weightCache.Count > 0 && _weightCache.Peek().Timestamp < expireTime)
            {
                _weightCache.Dequeue();
                removedCount++;
            }

            if (removedCount > 0)
            {
                Log.Debug("清理了 {Count} 条过期重量缓存数据", removedCount);
            }
        }
    }

    private static string ReverseWeight(string weightStr)
    {
        var chars = weightStr.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }

    /// <summary>
    /// 定期状态检查方法
    /// </summary>
    private void OnStatusCheck(object? sender, ElapsedEventArgs e)
    {
        try
        {
            var now = DateTime.Now;
            var timeSinceLastData = _lastDataReceiveTime == DateTime.MinValue ? 
                TimeSpan.Zero : now - _lastDataReceiveTime;
                
            Log.Information("串口状态检查 - 连接状态: {Connected}, 最后接收数据: {LastReceive}, 距离上次: {TimeSince:F1}秒, 缓存数量: {CacheCount}", 
                _serialPortService.IsConnected, 
                _lastDataReceiveTime == DateTime.MinValue ? "从未接收" : _lastDataReceiveTime.ToString("HH:mm:ss.fff"),
                timeSinceLastData.TotalSeconds,
                _weightCache.Count);
                
            // 如果超过30秒没有收到数据且连接状态显示正常，发出警告
            if (_serialPortService.IsConnected && timeSinceLastData.TotalSeconds > 30 && _lastDataReceiveTime != DateTime.MinValue)
            {
                Log.Warning("串口连接正常但超过30秒未收到数据，可能存在通信问题");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "状态检查时发生错误");
        }
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            Stop();
            
            _statusCheckTimer?.Stop();
            _statusCheckTimer?.Dispose();

            _serialPortService.Dispose();

            _weightReceived.Dispose();
        }

        _disposed = true;
    }
}