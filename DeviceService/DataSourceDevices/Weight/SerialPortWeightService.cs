using System.Globalization;
using System.Text;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.SerialPort; // 引入新服务的命名空间
using Serilog;
using System.Reactive.Subjects; // 添加 Reactive Subjects 命名空间

// 如果尚未存在，添加LINQ命名空间

namespace DeviceService.DataSourceDevices.Weight;

/// <summary>
///     串口重量称服务 (已重构为使用 SerialPortService)
/// </summary>
public class SerialPortWeightService : IDisposable
{
    // 常量定义
    private const double StableThreshold = 0.001;
    private const int ProcessInterval = 100;

    private readonly object _lock = new();
    private readonly List<double> _weightSamples = [];
    private readonly StringBuilder _receiveBuffer = new();
    private readonly SerialPortService _serialPortService;
    private readonly ISettingsService _settingsService;
    private readonly Subject<(double Weight, DateTime Timestamp, WeightType Type)> _weightDataSubject = new();

    private bool _disposed;
    private bool _isConnected;
    private DateTime _lastProcessTime = DateTime.MinValue;

    public SerialPortWeightService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        var weightSettings = _settingsService.LoadSettings<WeightSettings>();
        _serialPortService = new SerialPortService("WeightScale", weightSettings.SerialPortParams);
    }

    public IObservable<(double Weight, DateTime Timestamp, WeightType Type)> WeightDataStream => _weightDataSubject;

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (_isConnected == value) return;
            _isConnected = value;
            ConnectionChanged?.Invoke("称重模块 Scale", value);
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public event Action<string, bool>? ConnectionChanged;

    public bool Start()
    {
        try
        {
            Log.Information("正在启动串口重量称服务 (通过 SerialPortService)...");

            _serialPortService.ConnectionChanged -= HandleConnectionStatusChanged;
            _serialPortService.DataReceived -= HandleDataReceived;
            _serialPortService.ConnectionChanged += HandleConnectionStatusChanged;
            _serialPortService.DataReceived += HandleDataReceived;

            var connectResult = _serialPortService.Connect();
            if (connectResult) return connectResult;
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

    public void Stop()
    {
        try
        {
            _serialPortService.ConnectionChanged -= HandleConnectionStatusChanged;
            _serialPortService.DataReceived -= HandleDataReceived;

            _serialPortService.Disconnect();

            lock (_lock)
            {
                _receiveBuffer.Clear();
            }
            _weightSamples.Clear();
            Log.Information("串口重量称服务已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止串口重量称服务时发生错误");
        }
    }

    private void HandleConnectionStatusChanged(bool isConnected)
    {
        Log.Debug("SerialPortWeightService - 收到连接状态变更: {IsConnected}", isConnected);
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

            lock (_lock)
            {
                _receiveBuffer.Append(receivedString);

                const int maxInternalBufferSize = 4096;
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
        _weightSamples.Add(weightG);

        while (_weightSamples.Count > _settingsService.LoadSettings<WeightSettings>().StableCheckCount) _weightSamples.RemoveAt(0);

        if (_weightSamples.Count < _settingsService.LoadSettings<WeightSettings>().StableCheckCount) return;

        var average = _weightSamples.Average();
        const double stableThresholdG = StableThreshold * 1000;
        var isStable = _weightSamples.All(w => Math.Abs(w - average) <= stableThresholdG);

        if (!isStable)
        {
            var samplesString = string.Join(", ", _weightSamples.Select(w => w.ToString("F2")));
            Log.Verbose("重量样本不稳定: Avg={Avg:F2}g, Samples=[{Samples}]", average, samplesString);
            return;
        }

        var localTimestamp = timestamp.Kind == DateTimeKind.Local ? timestamp : timestamp.ToLocalTime();
        _weightDataSubject.OnNext((average, localTimestamp, WeightType.Static));

        _weightSamples.Clear();
    }

    private void ProcessDynamicWeight(double weightG, DateTime timestamp)
    {
        _weightDataSubject.OnNext((weightG, timestamp, WeightType.Dynamic));
    }

    private static string ReverseWeight(string weightStr)
    {
        var chars = weightStr.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;


        if (disposing)
        {
            Stop();

            _serialPortService.Dispose();

            _weightDataSubject.OnCompleted();
            _weightDataSubject.Dispose();
        }

        _disposed = true;
    }
}