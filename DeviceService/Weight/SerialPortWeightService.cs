using System.IO.Ports;
using System.Reactive.Subjects;
using CommonLibrary.Models.Settings.Weight;
using Serilog;

namespace DeviceService.Weight;

/// <summary>
/// 串口重量称服务
/// </summary>
public class SerialPortWeightService : IWeightService
{
    // 常量定义
    private const int MaxCacheSize = 100;
    private const int ReadBufferSize = 4096;
    private const int MaxCacheAgeMinutes = 2;
    private const double StableThreshold = 0.001;
    private const int ProcessInterval = 50;

    private readonly Subject<float> _weightSubject = new();
    private readonly object _lock = new();
    private readonly byte[] _readBuffer = new byte[ReadBufferSize];
    private readonly Queue<(double Weight, DateTime Timestamp)> _weightCache = new();
    private readonly List<double> _weightSamples = [];
    private readonly AutoResetEvent _weightReceived = new(false);
    private readonly CancellationTokenSource _cts = new();

    private SerialPort? _serialPort;
    private WeightSettings _settings = new();
    private bool _disposed;
    private bool _isConnected;
    private int _bufferPosition;
    private DateTime _lastProcessTime = DateTime.MinValue;

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

    public event Action<string, bool>? ConnectionChanged;

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
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
            await StopAsync();

            // 5. 同步创建并打开串口
            lock (_lock)
            {
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
                        Encoding = System.Text.Encoding.ASCII,
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
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动串口重量称服务失败");
            return false;
        }
    }

    /// <summary>
    /// 检查串口是否被占用
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

    public Task StopAsync()
    {
        try
        {
            Log.Information("正在停止串口重量称服务...");

            if (_serialPort == null) return Task.CompletedTask;

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
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "关闭串口时发生错误");
                }
            }

            Log.Information("串口重量称服务已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止串口重量称服务时发生错误");
        }

        return Task.CompletedTask;
    }

    public Task UpdateConfigurationAsync(WeightSettings config)
    {
        _settings = config;
        return Task.CompletedTask;
    }

    public double? FindNearestWeight(DateTime targetTime)
    {
        lock (_lock)
        {
            var lowerBound = targetTime.AddMilliseconds(-_settings.TimeRangeLower);
            var upperBound = targetTime.AddMilliseconds(_settings.TimeRangeUpper);

            if (_weightCache.Count > 0)
            {
                var nearestWeight = _weightCache
                    .Where(w => w.Timestamp <= targetTime)
                    .OrderByDescending(w => w.Timestamp)
                    .FirstOrDefault();

                if (nearestWeight != default &&
                    nearestWeight.Timestamp >= lowerBound &&
                    nearestWeight.Timestamp <= upperBound)
                {
                    var timeDiff = (nearestWeight.Timestamp - targetTime).TotalMilliseconds;
                    Log.Debug("Found historical weight data: {Weight:F3}kg, Time diff: {TimeDiff:F0}ms",
                        nearestWeight.Weight / 1000, timeDiff);
                    return nearestWeight.Weight;
                }
            }

            if (DateTime.Now > upperBound)
            {
                Log.Debug("Current time has exceeded the upper limit, no longer waiting for new data");
                return null;
            }

            var waitTime = upperBound - DateTime.Now;
            if (waitTime <= TimeSpan.Zero) return null;

            Log.Debug("Waiting for new weight data, max wait: {WaitTime:F0}ms", waitTime.TotalMilliseconds);

            var remainingTime = waitTime;
            while (remainingTime > TimeSpan.Zero)
            {
                var currentWaitTime = TimeSpan.FromMilliseconds(Math.Min(100, remainingTime.TotalMilliseconds));
                if (_weightReceived.WaitOne(currentWaitTime))
                {
                    var nearestWeight = _weightCache
                        .Where(w => w.Timestamp <= targetTime)
                        .OrderByDescending(w => w.Timestamp)
                        .FirstOrDefault();

                    if (nearestWeight != default && nearestWeight.Timestamp >= lowerBound)
                    {
                        var timeDiff = (nearestWeight.Timestamp - targetTime).TotalMilliseconds;
                        Log.Debug("Found weight data after waiting: {Weight:F3}kg, Time diff: {TimeDiff:F0}ms",
                            nearestWeight.Weight / 1000, timeDiff);
                        return nearestWeight.Weight;
                    }
                }

                remainingTime = upperBound - DateTime.Now;
            }

            Log.Debug("Wait timed out, no weight data found within the time range");
            return null;
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
            var data = System.Text.Encoding.ASCII.GetString(_readBuffer, 0, _bufferPosition).Trim();
            if (!float.TryParse(data, out var weight)) return;
            if (_settings.WeightType == WeightType.Static)
            {
                ProcessStaticWeight(weight * 1000, receiveTime); // Convert to grams
            }
            else
            {
                ProcessDynamicWeight(weight * 1000, receiveTime); // Convert to grams
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

        while (_weightSamples.Count > _settings.StableCheckCount)
        {
            _weightSamples.RemoveAt(0);
        }

        if (_weightSamples.Count < _settings.StableCheckCount) return;

        var average = _weightSamples.Average();
        var isStable = _weightSamples.All(w => Math.Abs(w - average) <= StableThreshold * 1000);

        if (!isStable) return;
        lock (_lock)
        {
            _weightCache.Enqueue((average, timestamp));
            while (_weightCache.Count > MaxCacheSize)
            {
                _weightCache.Dequeue();
            }
        }

        _weightSubject.OnNext((float)(average / 1000)); // Convert back to kilograms
        _weightSamples.Clear();
        _weightReceived.Set();
    }

    private void ProcessDynamicWeight(double weightG, DateTime timestamp)
    {
        lock (_lock)
        {
            _weightCache.Enqueue((weightG, timestamp));
            while (_weightCache.Count > MaxCacheSize)
            {
                _weightCache.Dequeue();
            }
        }

        _weightSubject.OnNext((float)(weightG / 1000)); // Convert back to kilograms
        _weightReceived.Set();
    }

    private void CleanExpiredWeightData(DateTime currentTime)
    {
        lock (_lock)
        {
            var expireTime = currentTime.AddMinutes(-MaxCacheAgeMinutes);
            while (_weightCache.Count > 0 && _weightCache.Peek().Timestamp < expireTime)
            {
                _weightCache.Dequeue();
            }
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

    public void Dispose()
    {
        if (_disposed) return;

        try
        {
            _cts.Cancel();
            StopAsync().GetAwaiter().GetResult();
            _weightSubject.Dispose();
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        try
        {
            await _cts.CancelAsync();
            await StopAsync();
            _weightSubject.Dispose();
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
}