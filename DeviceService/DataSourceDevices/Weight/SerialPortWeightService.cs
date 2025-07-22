using System.Diagnostics;
using System.Globalization;
using System.Text;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.SerialPort;
using Microsoft.VisualBasic;
using Serilog;
// 引入新服务的命名空间

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
    private const double StableThreshold = 0.01; // 0.01kg = 10g
    private const int ProcessInterval = 100;

    private readonly object _lock = new();
    private readonly List<byte> _receiveBuffer = new();
    private readonly SerialPortService _serialPortService;
    private readonly ISettingsService _settingsService;
    private readonly Queue<(double Weight, DateTime Timestamp)> _weightCache = new();
    private readonly AutoResetEvent _weightReceived = new(false);
    private readonly List<double> _weightSamples = [];

    private bool _disposed;
    private bool _isConnected;
    private DateTime _lastDataReceiveTime = DateTime.MinValue;
    private DateTime _lastProcessTime = DateTime.MinValue;

    public SerialPortWeightService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        var weightSettings = _settingsService.LoadSettings<WeightSettings>();
        _serialPortService = new SerialPortService("WeightScale", weightSettings.SerialPortParams);
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

    /// <summary>
    /// 按需执行连接状态检查和验证
    /// 这个方法在需要时才检查连接状态，实现事件驱动 + 按需检查的策略
    /// </summary>
    private void PerformOnDemandConnectionCheck()
    {
        try
        {
            // 智能检查连接状态不一致的情况
            if (IsConnected != _serialPortService.IsConnected)
            {
                Log.Warning("发现连接状态不一致 - 本地: {Local}, 底层: {Service}", IsConnected, _serialPortService.IsConnected);

                switch (_serialPortService.IsConnected)
                {
                    // 如果底层服务显示已连接，但本地状态显示断开，优先信任底层服务
                    case true when !IsConnected:
                        Log.Information("底层服务显示已连接，同步本地状态为已连接");
                        IsConnected = true;
                        break;
                    // 如果底层服务显示断开，但本地状态显示连接，需要进一步验证
                    case false when IsConnected:
                    {
                        Log.Warning("底层服务显示断开，但本地状态显示连接，尝试重连验证");
                        var reconnectResult = _serialPortService.Connect();
                        Log.Information("重连验证结果: {Result}", reconnectResult);
                        IsConnected = _serialPortService.IsConnected;
                        break;
                    }
                }
            }

            // 如果连接确实断开，尝试重连一次
            if (!_serialPortService.IsConnected)
            {
                Log.Warning("发现串口连接断开，尝试重连...");
                var reconnectResult = _serialPortService.Connect();
                Log.Information("按需检查期间的重连结果: {Result}", reconnectResult);
                
                // 更新本地连接状态
                IsConnected = _serialPortService.IsConnected;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "按需连接状态检查时发生错误");
            // 发生异常时，将连接状态设为断开
            IsConnected = false;
        }
    }

    public double? FindNearestWeight(DateTime targetTime)
    {
        PerformOnDemandConnectionCheck();

        Log.Debug("FindNearestWeight 开始 - 目标时间: {TargetTime}", targetTime);

        var settings = _settingsService.LoadSettings<WeightSettings>();
        var lowerBound = targetTime.AddMilliseconds(settings.TimeRangeLower);
        var upperBound = targetTime.AddMilliseconds(settings.TimeRangeUpper);

        (double Weight, DateTime Timestamp)? nearestZeroWeight = null;

        // 1. 初始查找（在短暂的锁中完成）
        lock (_lock)
        {
            var initialWeightInRange = _weightCache
                .Where(w => w.Timestamp >= lowerBound && w.Timestamp <= upperBound)
                .OrderBy(w => Math.Abs((w.Timestamp - targetTime).TotalMilliseconds))
                .FirstOrDefault();

            if (initialWeightInRange != default)
            {
                var timeDiff = (initialWeightInRange.Timestamp - targetTime).TotalMilliseconds;
                if (initialWeightInRange.Weight != 0)
                {
                    Log.Debug("初始查找找到非零重量数据: {Weight:F3}kg, 时间差: {TimeDiff:F0}ms",
                        initialWeightInRange.Weight / 1000, timeDiff);
                    return initialWeightInRange.Weight;
                }
                
                Log.Debug("初始查找找到零重量数据，记录并继续等待: 时间差: {TimeDiff:F0}ms", timeDiff);
                nearestZeroWeight = initialWeightInRange;
            }
        }

        // 2. 如果没有立即找到非零值，则在锁外部等待
        var waitTime = upperBound - DateTime.Now;
        if (waitTime <= TimeSpan.Zero)
        {
            if (nearestZeroWeight.HasValue)
            {
                Log.Debug("等待时间已过，返回之前找到的零重量");
                return nearestZeroWeight.Value.Weight;
            }
            Log.Debug("等待时间已过，未找到任何数据");
            return null;
        }

        Log.Debug("未立即找到非零重量，开始等待，最大等待时间: {WaitTime:F0}ms", waitTime.TotalMilliseconds);

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < waitTime && IsConnected)
        {
            var remainingTime = waitTime - sw.Elapsed;
            if (remainingTime <= TimeSpan.Zero) break;

            var currentWaitTimeout = TimeSpan.FromMilliseconds(Math.Min(100, remainingTime.TotalMilliseconds));

            if (_weightReceived.WaitOne(currentWaitTimeout))
            {
                // 收到信号后，短暂加锁检查缓存
                lock (_lock)
                {
                    var weightInRangeDuringWait = _weightCache
                        .Where(w => w.Timestamp >= lowerBound && w.Timestamp <= upperBound)
                        .OrderByDescending(w => w.Timestamp) // 优先最新的
                        .ToList();

                    var nonZeroWeight = weightInRangeDuringWait.FirstOrDefault(w => w.Weight != 0);
                    if (nonZeroWeight != default)
                    {
                        sw.Stop();
                        var timeDiff = (nonZeroWeight.Timestamp - targetTime).TotalMilliseconds;
                        Log.Debug("等待后找到符合条件的非零重量数据: {Weight:F3}kg, 时间差: {TimeDiff:F0}ms",
                            nonZeroWeight.Weight / 1000, timeDiff);
                        return nonZeroWeight.Weight;
                    }

                    var zeroWeight = weightInRangeDuringWait.FirstOrDefault(w => w.Weight == 0);
                    if (zeroWeight != default && (!nearestZeroWeight.HasValue || 
                        Math.Abs((zeroWeight.Timestamp - targetTime).TotalMilliseconds) < 
                        Math.Abs((nearestZeroWeight.Value.Timestamp - targetTime).TotalMilliseconds)))
                    {
                        Log.Debug("等待期间更新了最佳零重量记录");
                        nearestZeroWeight = zeroWeight;
                    }
                }
            }
        }

        sw.Stop();

        // 3. 等待结束，返回最终结果
        if (nearestZeroWeight.HasValue)
        {
            Log.Debug("等待结束 ({Elapsed}ms)，未找到非零重量，返回找到的最佳零重量", sw.ElapsedMilliseconds);
            return nearestZeroWeight.Value.Weight;
        }

        Log.Debug("等待结束 ({Elapsed}ms)，未找到任何符合条件的重量数据", sw.ElapsedMilliseconds);
        return null;
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
            // 将二进制数据转换为十六进制格式显示，便于调试HK协议等二进制协议
            var hexData = BitConverter.ToString(data).Replace("-", " ");
            Log.Debug("SerialPortWeightService - 收到原始数据: {Data}, 长度: {Length}, 连接状态: {Connected}, 时间: {Time}",
                hexData, data.Length, _serialPortService.IsConnected, DateAndTime.Now);
            
            // 仅用于内部处理的ASCII字符串转换
            var receivedString = Encoding.ASCII.GetString(data);

            // 更新最后接收数据的时间
            _lastDataReceiveTime = DateTime.Now;

            // 如果收到数据但连接状态显示断开，这是异常情况，主动修复
            if (!_serialPortService.IsConnected)
            {
                Log.Warning("收到数据但底层服务状态为断开，此为异常状态，将尝试强制重新连接以同步状态...");
                _serialPortService.Disconnect(); // 尝试清理
                var reconnectResult = _serialPortService.Connect(); // 重建连接和状态
                Log.Information("数据接收期间的重连尝试结果: {Result}", reconnectResult);
            }

            lock (_lock)
            {
                _receiveBuffer.AddRange(data);
                Log.Debug("当前接收缓冲区内容: {BufferContent}, 长度: {Length}", BitConverter.ToString([.. _receiveBuffer]).Replace("-", " "), _receiveBuffer.Count);

                const int maxInternalBufferSize = 4096; // 定义一个合适的缓冲区大小
                if (_receiveBuffer.Count > maxInternalBufferSize)
                {
                    Log.Warning("内部接收缓冲区超过限制 ({Length} > {Limit})，清空缓冲区", _receiveBuffer.Count, maxInternalBufferSize);
                    _receiveBuffer.Clear();
                    return;
                }

                ProcessBuffer(DateTime.Now);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理接收到的串口数据时发生错误");
            
            // 根据异常类型判断是否需要处理连接问题
            if (ShouldTriggerReconnectionOnException(ex))
            {
                Log.Warning("数据处理异常可能指示连接问题，触发事件驱动的重连检查");
                TriggerEventDrivenReconnection();
            }
            
            lock (_lock)
            {
                _receiveBuffer.Clear();
            }
        }
    }

    /// <summary>
    ///     处理内部缓冲区的重量数据 (需要调整以使用 StringBuilder)
    /// </summary>
    private void ProcessBuffer(DateTime receiveTime)
    {
        CleanExpiredWeightData(receiveTime);

        var weightSettings = _settingsService.LoadSettings<WeightSettings>();
        
        // 根据称重类型选择协议解析方式
        if (weightSettings.WeightType == WeightType.Dynamic)
        {
            // 动态称重：只使用HK协议解析
            ProcessHkProtocol(receiveTime);
        }
        else
        {
            // 静态称重：只使用=协议解析
            ProcessEqualsProtocol(receiveTime);
        }
    }

    /// <summary>
    /// 处理HK协议数据（仅用于动态称重）
    /// </summary>
    private void ProcessHkProtocol(DateTime receiveTime)
    {
        var bufferBytes = _receiveBuffer.ToArray();
        Log.Debug("ProcessHkProtocol: 缓冲区字节数据 - 长度: {Length}, 十六进制: {HexData}", 
            bufferBytes.Length, BitConverter.ToString(bufferBytes));
        
        int i = 0;
        bool foundHkFrame = false;
        while (i <= bufferBytes.Length - 8)
        {
            if (bufferBytes[i] == 0x88 && bufferBytes[i + 1] == 0x02 && bufferBytes[i + 7] == 0x16)
            {
                foundHkFrame = true;
                var weightBytes = bufferBytes.Skip(i + 2).Take(5).ToArray();
                double weight = ParseHkWeight(weightBytes); // 单位kg
                Log.Debug("ProcessHkProtocol: 找到HK协议帧，解析重量: {Weight}kg", weight);
                
                // 将kg转换为g并处理动态重量
                ProcessDynamicWeight(weight * 1000, receiveTime);
                i += 8; // 跳过本帧
            }
            else
            {
                i++;
            }
        }
        
        if (bufferBytes.Length >= 8 && !foundHkFrame)
        {
            Log.Debug("ProcessHkProtocol: 未找到HK协议帧，数据不符合HK格式 (需要0x88 0x02开头，0x16结尾)");
        }
        
        // 清理已处理内容
        if (i > 0)
        {
            _receiveBuffer.RemoveRange(0, Math.Min(i, _receiveBuffer.Count));
        }
    }

    /// <summary>
    /// 处理=协议数据（仅用于静态称重）
    /// </summary>
    private void ProcessEqualsProtocol(DateTime receiveTime)
    {
        // 将字节转换为字符串进行=协议处理
        var bufferContent = Encoding.ASCII.GetString([.. _receiveBuffer]);
        if (string.IsNullOrEmpty(bufferContent)) 
        {
            Log.Debug("ProcessEqualsProtocol: 缓冲区为空，跳过=协议处理");
            return;
        }

        Log.Debug("ProcessEqualsProtocol: 开始=协议处理，缓冲区内容: \"{Content}\", 长度: {Length}", 
            bufferContent, bufferContent.Length);

        var processedLength = 0;

        try
        {
            var lastSeparatorIndex = bufferContent.LastIndexOf('=');

            if (lastSeparatorIndex == -1)
            {
                Log.Debug("ProcessEqualsProtocol: 缓冲区中未找到分隔符 '='，数据不符合=协议格式");
                if (_receiveBuffer.Count <= 100) 
                {
                    Log.Debug("ProcessEqualsProtocol: 缓冲区长度 {Length} <= 100，继续等待更多数据", _receiveBuffer.Count);
                    return;
                }
                Log.Warning("ProcessEqualsProtocol: 缓冲区过长但无分隔符，可能数据格式错误，清空缓冲区");
                _receiveBuffer.Clear();
                return;
            }

            var dataToProcess = bufferContent[..lastSeparatorIndex];
            processedLength = lastSeparatorIndex + 1;

            Log.Verbose("ProcessEqualsProtocol: 准备处理的数据段: {DataSegment}", dataToProcess);

            var dataSegments = dataToProcess.Split(['='], StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            foreach (var segment in dataSegments)
            {
                if (segment.Length < 3)
                {
                    Log.Warning("ProcessEqualsProtocol: 无效的重量数据段 (太短): \"{Segment}\"", segment);
                    continue;
                }

                var valuePart = segment.Length >= 6 ? segment[..6] : segment;
                var reversedValue = ReverseWeight(valuePart);
                Log.Verbose("ProcessEqualsProtocol: 解析反转后的值: {ReversedValue} (来自段: {Segment})", reversedValue, valuePart);

                if (float.TryParse(reversedValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var weightInKg))
                {
                    var weightG = weightInKg * 1000;
                    ProcessStaticWeight(weightG, receiveTime);
                }
                else
                {
                    Log.Warning("ProcessEqualsProtocol: 无法解析反转后的重量数据: {ReversedData} (原始段: {OriginalSegment})", reversedValue, valuePart);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ProcessEqualsProtocol: 解析重量数据缓冲区时发生错误");
            _receiveBuffer.Clear();
        }
        finally
        {
            if (processedLength > 0 && processedLength <= _receiveBuffer.Count)
            {
                Log.Debug("ProcessEqualsProtocol: 从缓冲区移除已处理的 {ProcessedLength} 个字节", processedLength);
                _receiveBuffer.RemoveRange(0, processedLength);
                Log.Debug("ProcessEqualsProtocol: 处理后剩余缓冲区内容: \"{RemainingBuffer}\"", BitConverter.ToString([.. _receiveBuffer]).Replace("-", " "));
            }
            else if (processedLength > _receiveBuffer.Count)
            {
                Log.Warning("ProcessEqualsProtocol: 计算的处理长度 {ProcessedLength} 大于缓冲区长度 {BufferLength}，清空缓冲区", 
                    processedLength, _receiveBuffer.Count);
                _receiveBuffer.Clear();
            }
            
            // 总结数据处理结果
            Log.Debug("ProcessEqualsProtocol: 数据处理完成 - 当前重量缓存数量: {CacheCount}", _weightCache.Count);
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

        var stableCheckCount = _settingsService.LoadSettings<WeightSettings>().StableCheckCount;
        while (_weightSamples.Count > stableCheckCount)
        {
            _weightSamples.RemoveAt(0);
            Log.Debug("移除最旧样本，当前样本数: {Count}", _weightSamples.Count);
        }

        if (_weightSamples.Count < stableCheckCount)
        {
            Log.Debug("样本数量不足，需要 {Required} 个样本，当前: {Current}",
                stableCheckCount,
                _weightSamples.Count);
            return;
        }

        // 改为滑动窗口判断：检查窗口中最后一个值与所有先前值的绝对差是否均小于阈值
        var lastWeight = _weightSamples[^1]; // 使用C# 8.0的索引器获取最后一个元素
        const double stableThresholdG = StableThreshold * 1000;

        // LINQ表达式：取窗口中除最后一个元素外的所有元素，然后检查它们是否都满足稳定条件
        var isStable = _weightSamples.Take(_weightSamples.Count - 1).All(w => Math.Abs(w - lastWeight) <= stableThresholdG);

        if (!isStable)
        {
            var samplesString = string.Join(", ", _weightSamples.Select(w => w.ToString("F2")));
            Log.Debug("重量样本不稳定 (滑动窗口检查): Last={Last:F2}g, 阈值={Threshold:F2}g, Samples=[{Samples}]",
                lastWeight, stableThresholdG, samplesString);
            return;
        }

        Log.Debug("重量稳定 (滑动窗口): {Weight:F2}g, 样本数: {Count}", lastWeight, _weightSamples.Count);

        lock (_lock)
        {
            _weightCache.Enqueue((lastWeight, timestamp)); // 使用最后一个稳定的重量值
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

    // HK协议5字节重量解析，单位kg
    private static double ParseHkWeight(byte[] weightBytes)
    {
        if (weightBytes.Length != 5) return 0;
        var str = string.Concat(weightBytes.Select(b => (b % 10).ToString()));
        if (str.Length < 5) str = str.PadLeft(5, '0');
        str = str.Insert(str.Length - 2, ".");
        return double.TryParse(str, out var v) ? v : 0;
    }

    /// <summary>
    /// 判断异常是否应该触发重连检查
    /// </summary>
    /// <param name="ex">捕获的异常</param>
    /// <returns>是否应该触发重连</returns>
    private static bool ShouldTriggerReconnectionOnException(Exception ex)
    {
        return ex switch
        {
            TimeoutException => true,           // 超时异常可能指示连接问题
            InvalidOperationException => true,  // 操作异常可能指示端口状态问题
            UnauthorizedAccessException => true, // 访问被拒绝可能指示端口被占用
            IOException => true,      // IO异常通常指示连接问题
            _ => false                          // 其他异常不触发重连
        };
    }

    /// <summary>
    /// 触发事件驱动的重连检查
    /// 这个方法在检测到可能的连接问题时被调用
    /// </summary>
    private void TriggerEventDrivenReconnection()
    {
        try
        {
            Log.Information("触发事件驱动的连接状态检查和重连");
            
            // 首先断开当前连接
            if (_serialPortService.IsConnected)
            {
                Log.Debug("断开当前连接以进行重连");
                _serialPortService.Disconnect();
            }
            
            // 尝试重新连接
            var reconnectResult = _serialPortService.Connect();
            Log.Information("事件驱动重连结果: {Result}", reconnectResult);
            
            // 更新本地连接状态
            IsConnected = _serialPortService.IsConnected;
            
            if (reconnectResult)
            {
                // 重连成功后，重置数据接收时间
                _lastDataReceiveTime = DateTime.Now;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "事件驱动重连过程中发生错误");
            IsConnected = false;
        }
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            Stop();

            _serialPortService.Dispose();

            _weightReceived.Dispose();
        }

        _disposed = true;
    }
}