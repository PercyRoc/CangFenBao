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
            
            if (string.IsNullOrEmpty(_settings.SerialPortParams.PortName))
            {
                Log.Error("串口名称未配置");
                return false;
            }

            await StopAsync();
            
            // 等待一段时间确保串口资源完全释放
            await Task.Delay(500, cancellationToken);

            return await Task.Run(() =>
            {
                lock (_lock)
                {
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

                    _serialPort.DataReceived += OnDataReceived;
                    _serialPort.ErrorReceived += OnErrorReceived;

                    Log.Information("正在打开串口 {PortName}...", _serialPort.PortName);
                    _serialPort.Open();
                    IsConnected = true;
                    Log.Information("串口 {PortName} 已打开", _serialPort.PortName);
                    return true;
                }
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动串口重量称服务失败");
            return false;
        }
    }

    public async Task StopAsync()
    {
        try
        {
            Log.Information("正在停止串口重量称服务...");
            
            if (_serialPort == null) return;

            IsConnected = false;

            await Task.Run(() =>
            {
                lock (_lock)
                {
                    // 移除事件处理器
                    _serialPort.DataReceived -= OnDataReceived;
                    _serialPort.ErrorReceived -= OnErrorReceived;

                    if (_serialPort.IsOpen)
                    {
                        _serialPort.Close();
                    }

                    _serialPort.Dispose();
                    _serialPort = null;
                    _bufferPosition = 0;
                }
            });
            
            Log.Information("串口重量称服务已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止串口重量称服务时发生错误");
        }
    }

    public async Task UpdateConfigurationAsync(WeightSettings config)
    {
        ArgumentNullException.ThrowIfNull(config);
        
        try
        {
            var needReconnect = _serialPort is not { IsOpen: true } ||
                                (_serialPort.PortName == config.SerialPortParams.PortName &&
                                 _serialPort.BaudRate == config.SerialPortParams.BaudRate &&
                                 _serialPort.DataBits == config.SerialPortParams.DataBits &&
                                 _serialPort.StopBits == config.SerialPortParams.StopBits &&
                                 _serialPort.Parity == config.SerialPortParams.Parity);

            _settings = config;

            if (needReconnect)
            {
                Log.Information("串口参数已变更，重新连接...");
                await StopAsync();
                await StartAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新串口重量称配置时发生错误");
        }
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
                    Log.Debug("找到历史重量数据: {Weight:F3}kg, 时间差: {TimeDiff:F0}ms",
                        nearestWeight.Weight / 1000, timeDiff);
                    return nearestWeight.Weight;
                }
            }

            if (DateTime.Now > upperBound)
            {
                Log.Debug("当前时间已超过上限，不再等待新数据");
                return null;
            }

            var waitTime = upperBound - DateTime.Now;
            if (waitTime <= TimeSpan.Zero) return null;

            Log.Debug("等待新的重量数据，最多等待: {WaitTime:F0}ms", waitTime.TotalMilliseconds);

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
                        Log.Debug("等待后找到重量数据: {Weight:F3}kg, 时间差: {TimeDiff:F0}ms",
                            nearestWeight.Weight / 1000, timeDiff);
                        return nearestWeight.Weight;
                    }
                }

                remainingTime = upperBound - DateTime.Now;
            }

            Log.Debug("等待超时，未找到符合时间范围的重量数据");
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
                Log.Debug("缓冲区已满，重置缓冲区");
                _bufferPosition = 0;
            }

            var availableSpace = ReadBufferSize - _bufferPosition;
            var actualBytesToRead = Math.Min(bytesToRead, availableSpace);

            if (actualBytesToRead <= 0)
            {
                Log.Warning("缓冲区空间不足，跳过本次读取");
                return;
            }

            var bytesRead = _serialPort.Read(_readBuffer, _bufferPosition, actualBytesToRead);
            _bufferPosition += bytesRead;

            ProcessBuffer(receiveTime);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理串口数据时发生错误");
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
                ProcessStaticWeight(weight * 1000, receiveTime); // 转换为克
            }
            else
            {
                ProcessDynamicWeight(weight * 1000, receiveTime); // 转换为克
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "解析重量数据时发生错误");
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

        _weightSubject.OnNext((float)(average / 1000)); // 转换回千克
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

        _weightSubject.OnNext((float)(weightG / 1000)); // 转换回千克
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
                    Log.Warning("串口数据传输错误: {Error}, 端口: {PortName}", 
                        e.EventType, _serialPort?.PortName ?? "未知");
                    break;

                case SerialError.TXFull:
                    Log.Warning("串口发送缓冲区已满: {PortName}", 
                        _serialPort?.PortName ?? "未知");
                    break;

                case SerialError.RXOver:
                    Log.Warning("串口接收缓冲区溢出: {PortName}", 
                        _serialPort?.PortName ?? "未知");
                    if (_serialPort?.IsOpen == true)
                    {
                        _serialPort.DiscardInBuffer();
                        _bufferPosition = 0;
                    }
                    break;

                default:
                    Log.Error("串口发生严重错误: {Error}, 端口: {PortName}", 
                        e.EventType, _serialPort?.PortName ?? "未知");
                    IsConnected = false;
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理串口错误时发生异常");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        try
        {
            _cts.Cancel();
            StopAsync().Wait(TimeSpan.FromSeconds(3));
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