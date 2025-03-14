using System.Globalization;
using System.IO.Ports;
using System.Text;
using Common.Models.Settings.Weight;
using Serilog;

namespace DeviceService.DataSourceDevices.Weight;

/// <summary>
///     串口重量称服务
/// </summary>
public class SerialPortWeightService : IDisposable
{
    // 常量定义
    private const int MaxCacheSize = 100;
    private const int ReadBufferSize = 4096;
    private const int MaxCacheAgeMinutes = 2;
    private const double StableThreshold = 0.001;
    private const int ProcessInterval = 50;
    private readonly CancellationTokenSource _cts = new();

    private readonly object _lock = new();
    private readonly byte[] _readBuffer = new byte[ReadBufferSize];
    private readonly Queue<(double Weight, DateTime Timestamp)> _weightCache = new();
    private readonly AutoResetEvent _weightReceived = new(false);
    private readonly List<double> _weightSamples = [];
    private int _bufferPosition;
    private bool _disposed;
    private bool _isConnected;
    private DateTime _lastProcessTime = DateTime.MinValue;

    private SerialPort? _serialPort;
    private WeightSettings _settings = new();

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
        if (_disposed) return;

        try
        {
            _cts.Cancel();
            Stop();
            _weightReceived.Dispose();
            _cts.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "释放串口重量称资源时发生错误");
        }
        finally
        {
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    public event Action<string, bool>? ConnectionChanged;

    public bool Start()
    {
        try
        {
            Log.Information("正在启动串口重量称服务...");

            // 1. 验证串口配置
            if (string.IsNullOrEmpty(_settings.SerialPortParams.PortName))
            {
                Log.Error("串口名称未配置");
                return false;
            }

            // 2. 检查串口是否存在
            var availablePorts = SerialPort.GetPortNames();
            if (!availablePorts.Contains(_settings.SerialPortParams.PortName))
            {
                Log.Error("配置的串口 {PortName} 不存在", _settings.SerialPortParams.PortName);
                return false;
            }

            // 3. 停止现有连接
            Stop();
            try
            {
                Log.Debug("正在创建串口实例，参数：端口={Port}, 波特率={BaudRate}, 数据位={DataBits}, 停止位={StopBits}, 校验位={Parity}",
                    _settings.SerialPortParams.PortName,
                    _settings.SerialPortParams.BaudRate,
                    _settings.SerialPortParams.DataBits,
                    _settings.SerialPortParams.StopBits,
                    _settings.SerialPortParams.Parity);

                _serialPort = new SerialPort
                {
                    PortName = _settings.SerialPortParams.PortName,
                    BaudRate = _settings.SerialPortParams.BaudRate,
                    DataBits = _settings.SerialPortParams.DataBits,
                    StopBits = _settings.SerialPortParams.StopBits,
                    Parity = _settings.SerialPortParams.Parity,
                    Encoding = Encoding.ASCII,
                    ReadBufferSize = ReadBufferSize * 2,
                    ReadTimeout = 500,
                    WriteTimeout = 500
                };

                // 检查串口是否已被占用
                if (IsPortInUse(_settings.SerialPortParams.PortName))
                {
                    Log.Error("串口 {PortName} 已被其他程序占用", _settings.SerialPortParams.PortName);
                    return false;
                }

                _serialPort.DataReceived += OnDataReceived;
                _serialPort.ErrorReceived += OnErrorReceived;

                Log.Information("正在打开串口 {PortName}...", _serialPort.PortName);
                _serialPort.Open();
                IsConnected = true;
                Log.Information("串口 {PortName} 已打开", _serialPort.PortName);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                Log.Error("无权限访问串口 {PortName}", _settings.SerialPortParams.PortName);
                return false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "打开串口 {PortName} 时发生错误", _settings.SerialPortParams.PortName);
                return false;
            }
        }

        catch (Exception ex)
        {
            Log.Error(ex, "启动串口重量称服务失败");
            return false;
        }
    }

    public void Stop()
    {
        Log.Information("正在停止串口重量称服务...");

        if (_serialPort == null) return;

        IsConnected = false;

        lock (_lock)
        {
            try
            {
                // 移除事件处理器
                _serialPort.DataReceived -= OnDataReceived;
                _serialPort.ErrorReceived -= OnErrorReceived;

                if (_serialPort.IsOpen)
                {
                    Log.Debug("正在关闭串口 {PortName}...", _serialPort.PortName);
                    _serialPort.Close();
                    Log.Debug("串口 {PortName} 已关闭", _serialPort.PortName);
                }

                _serialPort.Dispose();
                _serialPort = null;
                _bufferPosition = 0;

                Log.Information("串口重量称服务已停止");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "关闭串口时发生错误");
            }
        }
    }

    public void UpdateConfiguration(WeightSettings config)
    {
        _settings = config;
    }

    public double? FindNearestWeight(DateTime targetTime)
    {
        lock (_lock)
        {
            // 修正时间范围计算：下限应该是减去正数，上限应该是加上正数
            var lowerBound = targetTime.AddMilliseconds(_settings.TimeRangeLower); // TimeRangeLower 已经是负数，所以用加法
            var upperBound = targetTime.AddMilliseconds(_settings.TimeRangeUpper);

            Log.Debug("查找重量数据 - 目标时间: {TargetTime}, 下限: {LowerBound}, 上限: {UpperBound}, 缓存数量: {CacheCount}",
                targetTime, lowerBound, upperBound, _weightCache.Count);

            if (_weightCache.Count > 0)
            {
                // 修改查询逻辑：查找时间范围内的所有数据，选择最接近目标时间的数据
                var weightInRange = _weightCache
                    .Where(w => w.Timestamp >= lowerBound && w.Timestamp <= upperBound)
                    .OrderBy(w => Math.Abs((w.Timestamp - targetTime).TotalMilliseconds))
                    .FirstOrDefault();

                if (weightInRange != default)
                {
                    var timeDiff = (weightInRange.Timestamp - targetTime).TotalMilliseconds;
                    Log.Debug("找到符合条件的重量数据: {Weight:F3}kg, 时间差: {TimeDiff:F0}ms",
                        weightInRange.Weight / 1000, timeDiff);
                    return weightInRange.Weight;
                }
                else
                {
                    // 记录最近的数据（即使不在范围内）用于调试
                    var nearestWeight = _weightCache
                        .OrderBy(w => Math.Abs((w.Timestamp - targetTime).TotalMilliseconds))
                        .First();
                    var timeDiff = (nearestWeight.Timestamp - targetTime).TotalMilliseconds;
                    Log.Debug("找到重量数据但超出时间范围: {Weight:F3}kg, 时间差: {TimeDiff:F0}ms, 时间戳: {Timestamp}",
                        nearestWeight.Weight / 1000, timeDiff, nearestWeight.Timestamp);
                }
            }
            else
            {
                Log.Debug("重量缓存为空");
            }

            if (DateTime.Now > upperBound)
            {
                Log.Debug("当前时间已超过上限，不再等待新数据");
                return null;
            }

            var waitTime = upperBound - DateTime.Now;
            if (waitTime <= TimeSpan.Zero) return null;

            Log.Debug("等待新的重量数据，最大等待时间: {WaitTime:F0}ms", waitTime.TotalMilliseconds);

            var remainingTime = waitTime;
            while (remainingTime > TimeSpan.Zero)
            {
                var currentWaitTime = TimeSpan.FromMilliseconds(Math.Min(100, remainingTime.TotalMilliseconds));
                if (_weightReceived.WaitOne(currentWaitTime))
                {
                    Log.Debug("收到新的重量数据，重新查找");
                    var weightInRange = _weightCache
                        .Where(w => w.Timestamp >= lowerBound && w.Timestamp <= upperBound)
                        .OrderBy(w => Math.Abs((w.Timestamp - targetTime).TotalMilliseconds))
                        .FirstOrDefault();

                    if (weightInRange != default)
                    {
                        var timeDiff = (weightInRange.Timestamp - targetTime).TotalMilliseconds;
                        Log.Debug("等待后找到符合条件的重量数据: {Weight:F3}kg, 时间差: {TimeDiff:F0}ms",
                            weightInRange.Weight / 1000, timeDiff);
                        return weightInRange.Weight;
                    }
                }

                remainingTime = upperBound - DateTime.Now;
            }

            Log.Debug("等待超时，未找到符合条件的重量数据");
            return null;
        }
    }

    /// <summary>
    ///     检查串口是否被占用
    /// </summary>
    private static bool IsPortInUse(string portName)
    {
        try
        {
            using var port = new SerialPort(portName);
            port.Open();
            port.Close();
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            if (_serialPort is not { IsOpen: true }) return;

            var receiveTime = DateTime.Now;
            var bytesToRead = _serialPort.BytesToRead;
            if (bytesToRead == 0) return;

            if (_bufferPosition + bytesToRead > ReadBufferSize)
            {
                Log.Debug("Buffer is full, resetting buffer");
                _bufferPosition = 0;
            }

            var availableSpace = ReadBufferSize - _bufferPosition;
            var actualBytesToRead = Math.Min(bytesToRead, availableSpace);

            if (actualBytesToRead <= 0)
            {
                Log.Warning("Buffer space insufficient, skipping this read");
                return;
            }

            var bytesRead = _serialPort.Read(_readBuffer, _bufferPosition, actualBytesToRead);
            _bufferPosition += bytesRead;

            ProcessBuffer(receiveTime);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error occurred while processing serial port data");
            _bufferPosition = 0;
        }
    }

    private void ProcessBuffer(DateTime receiveTime)
    {
        if ((receiveTime - _lastProcessTime).TotalMilliseconds < ProcessInterval) return;
        _lastProcessTime = receiveTime;

        CleanExpiredWeightData(receiveTime);

        try
        {
            var rawData = Encoding.ASCII.GetString(_readBuffer, 0, _bufferPosition);

            // 新增数据分割逻辑
            var dataSegments = rawData.Split(['='], StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length >= 6) // 最小有效长度检查
                .ToList();

            foreach (var valuePart in dataSegments.Select(segment => segment.Length > 6 ? segment[..6] : segment))
            {
                // 先反转数据，再解析
                var reversedValue = ReverseWeight(valuePart);
                if (float.TryParse(reversedValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var weight))
                {
                    // 根据称重类型处理
                    if (_settings.WeightType == WeightType.Static)
                        ProcessStaticWeight(weight * 1000, receiveTime);
                    else
                        ProcessDynamicWeight(weight * 1000, receiveTime);
                }
                else
                {
                    Log.Warning("无法解析的重量数据: {Data}", valuePart);
                }
            }

            // 新增粘包处理：保留未处理完的数据
            var lastSegment = dataSegments.LastOrDefault();
            if (lastSegment != null && rawData.EndsWith("="))
            {
                _bufferPosition = 0; // 完整处理时清空缓冲区
            }
            else if (lastSegment != null)
            {
                // 将未处理完的部分保留在缓冲区
                var remaining = rawData.Substring(rawData.LastIndexOf('=') + 1);
                var remainingBytes = Encoding.ASCII.GetBytes(remaining);
                Array.Copy(remainingBytes, _readBuffer, remainingBytes.Length);
                _bufferPosition = remainingBytes.Length;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error occurred while parsing weight data");
        }
        finally
        {
            _bufferPosition = 0;
        }
    }

    private void ProcessStaticWeight(double weightG, DateTime timestamp)
    {
        _weightSamples.Add(weightG);

        while (_weightSamples.Count > _settings.StableCheckCount) _weightSamples.RemoveAt(0);

        if (_weightSamples.Count < _settings.StableCheckCount) return;

        var average = _weightSamples.Average();
        var isStable = _weightSamples.All(w => Math.Abs(w - average) <= StableThreshold * 1000);

        if (!isStable) return;


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
        Log.Debug("处理动态重量: {Weight:F3}kg", weightG / 1000);

        lock (_lock)
        {
            _weightCache.Enqueue((weightG, timestamp));
            while (_weightCache.Count > MaxCacheSize) _weightCache.Dequeue();
            Log.Debug("已缓存动态重量数据，当前缓存数量: {CacheCount}", _weightCache.Count);
        }

        _weightReceived.Set();
    }

    private void CleanExpiredWeightData(DateTime currentTime)
    {
        lock (_lock)
        {
            var expireTime = currentTime.AddMinutes(-MaxCacheAgeMinutes);
            while (_weightCache.Count > 0 && _weightCache.Peek().Timestamp < expireTime) _weightCache.Dequeue();
        }
    }

    private void OnErrorReceived(object sender, SerialErrorReceivedEventArgs e)
    {
        try
        {
            switch (e.EventType)
            {
                case SerialError.Frame:
                case SerialError.Overrun:
                case SerialError.RXParity:
                    Log.Warning("Serial port data transmission error: {Error}, Port: {PortName}",
                        e.EventType, _serialPort?.PortName ?? "Unknown");
                    break;

                case SerialError.TXFull:
                    Log.Warning("Serial port transmit buffer is full: {PortName}",
                        _serialPort?.PortName ?? "Unknown");
                    break;

                case SerialError.RXOver:
                    Log.Warning("Serial port receive buffer overflow: {PortName}",
                        _serialPort?.PortName ?? "Unknown");
                    if (_serialPort?.IsOpen == true)
                    {
                        _serialPort.DiscardInBuffer();
                        _bufferPosition = 0;
                    }

                    break;

                default:
                    Log.Error("Serious error occurred on serial port: {Error}, Port: {PortName}",
                        e.EventType, _serialPort?.PortName ?? "Unknown");
                    IsConnected = false;
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Exception occurred while handling serial port error");
        }
    }

    /// <summary>
    ///     反转重量数据，直接从后往前重新排列所有字符
    ///     例如：02.7000 -> 0007.20
    /// </summary>
    private static string ReverseWeight(string weightStr)
    {
        // 直接将整个字符串反转
        var chars = weightStr.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }
}