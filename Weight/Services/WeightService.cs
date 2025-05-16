using Common.Services.Settings;
using Serilog;
using System.Globalization;
using System.IO.Ports;
using System.Reactive.Subjects;
using System.Text;
using Weight.Models;
using Weight.Models.Settings;

namespace Weight.Services;

public class WeightService : IWeightService
{
    private const string DeviceName = "称重模块 Scale"; // 设备名称，用于事件
    private const int ProcessIntervalMs = 100; // 数据处理间隔

    private readonly ISettingsService _settingsService;
    private WeightSettings _weightSettings;
    
    private SerialPort? _serialPort;
    private readonly StringBuilder _receiveBuffer = new();
    private readonly object _lock = new();
    private bool _isTryingToConnectOrDisconnect;

    private WeightData? _lastProcessedWeightData; // 用于 GetCurrentWeightAsync 返回最新处理的数据
    private DateTime _lastProcessTime = DateTime.MinValue; // 恢复此字段

    // Add the Subject for the data stream
    private readonly Subject<WeightData?> _weightDataSubject = new();
    public IObservable<WeightData?> WeightDataStream => _weightDataSubject;

    public event Action<string, bool>? ConnectionChanged;

    private bool _isConnected;
    public bool IsConnected 
    {
        get => _isConnected;
        private set
        {
            if (_isConnected == value) return;
            _isConnected = value;
            ConnectionChanged?.Invoke(DeviceName, _isConnected); // 在状态改变时触发事件
        }
    }

    public WeightService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        _weightSettings = _settingsService.LoadSettings<WeightSettings>();
        Log.Information("称重服务已初始化。设置: Port={Port}, Baud={Baud}, Type={Type}", 
            _weightSettings.PortName, _weightSettings.BaudRate, _weightSettings.WeightType);
    }

    public async Task ConnectAsync()
    {
        if (IsConnected || _isTryingToConnectOrDisconnect) return;
        _isTryingToConnectOrDisconnect = true;

        try
        {
            _weightSettings = _settingsService.LoadSettings<WeightSettings>();
            if (!_weightSettings.IsEnabled)
            {
                Log.Information("称重功能已禁用，跳过连接。");
                IsConnected = false;
                _weightDataSubject.OnNext(null);
                return;
            }

            Log.Information("尝试连接到称重设备 {PortName}@{BaudRate}...", _weightSettings.PortName, _weightSettings.BaudRate);
            
            _serialPort = new SerialPort(_weightSettings.PortName, _weightSettings.BaudRate, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 500,
                WriteTimeout = 500
            };

            await Task.Run(() => _serialPort.Open());

            if (_serialPort.IsOpen)
            {
                IsConnected = true; 
                _serialPort.DataReceived += SerialPort_DataReceived;
                Log.Information("称重设备连接成功: {PortName}", _weightSettings.PortName);
            }
            else
            {
                IsConnected = false; // 确保状态更新并触发事件（如果变化）
                Log.Warning("无法打开串口: {PortName}", _weightSettings.PortName);
                _weightDataSubject.OnNext(null); // Stream null if connection failed
            }
        }
        catch (Exception ex)
        {
            IsConnected = false; // 确保状态更新并触发事件（如果变化）
            Log.Error(ex, "连接到称重设备 {PortName} 失败。", _weightSettings.PortName);
            _serialPort?.Dispose();
            _serialPort = null;
            _weightDataSubject.OnNext(null); // Stream null on exception
        }
        finally
        {
            _isTryingToConnectOrDisconnect = false;
        }
    }

    public async Task DisconnectAsync()
    {
        if (!IsConnected || _isTryingToConnectOrDisconnect) return;
        _isTryingToConnectOrDisconnect = true;

        Log.Information("断开称重设备连接...");
        try
        {
            if (_serialPort != null)
            {
                _serialPort.DataReceived -= SerialPort_DataReceived;
                if (_serialPort.IsOpen)
                {
                    await Task.Run(() => _serialPort.Close());
                }
                _serialPort.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "断开称重设备连接时发生错误。");
        }
        finally
        {
            _serialPort = null;
            IsConnected = false; // IsConnected setter 将触发事件
            lock(_lock)
            {
                _receiveBuffer.Clear();
                _lastProcessedWeightData = null;
            }
            _weightDataSubject.OnNext(null); // Stream null when disconnected
            Log.Information("称重设备已断开连接。");
            _isTryingToConnectOrDisconnect = false;
        }
    }

    private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_serialPort is not { IsOpen: true }) return;
        try
        {
            int bytesToRead = _serialPort.BytesToRead;
            byte[] buffer = new byte[bytesToRead];
            _serialPort.Read(buffer, 0, bytesToRead);
            string receivedString = Encoding.ASCII.GetString(buffer);
            
            bool triggerProcess;
            lock (_lock)
            {
                _receiveBuffer.Append(receivedString);
                triggerProcess = _receiveBuffer.Length > 0;
                const int maxInternalBufferSize = 4096;
                if (_receiveBuffer.Length > maxInternalBufferSize)
                {
                    Log.Warning("称重服务内部接收缓冲区超过限制 ({Length} > {Limit})，清空缓冲区", 
                        _receiveBuffer.Length, maxInternalBufferSize);
                    _receiveBuffer.Clear();
                    triggerProcess = false; // Don't process if buffer was cleared due to overflow
                }
            }
            
            if(triggerProcess) ProcessReceivedData(DateTime.Now); // 传递 DateTime.Now
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理接收到的串口数据时发生错误");
        }
    }

    private void ProcessReceivedData(DateTime receiveTime) // 接受 receiveTime 参数
    {
        if ((receiveTime - _lastProcessTime).TotalMilliseconds < ProcessIntervalMs) return;
        _lastProcessTime = receiveTime;

        if (!IsConnected) // Check IsConnected again, as status might change
        {
            if (_lastProcessedWeightData == null) return; // Only stream null if it was previously not null
            _lastProcessedWeightData = null;
            _weightDataSubject.OnNext(null);
            return;
        }

        string currentBufferContent;
        lock (_lock)
        {
            if (_receiveBuffer.Length == 0) 
            {
                 // If buffer is empty, consider streaming null or last known good value based on requirements
                // For now, if dynamic and no data, we might want to signal it.
                if (_weightSettings.WeightType == WeightType.Dynamic && _lastProcessedWeightData != null && _lastProcessedWeightData.Value != 0) {
                     // Potentially set to 0 or a specific indicator if no new data for dynamic mode for a while
                } 
                return;
            }
            currentBufferContent = _receiveBuffer.ToString();
        }

        var processedLength = 0;
        try
        {
            int lastSeparatorIndex = currentBufferContent.LastIndexOf('=');

            if (lastSeparatorIndex == -1)
            {
                if (_receiveBuffer.Length <= 100) return; // 统一为100
                Log.Warning("称重缓冲区过长 ({Length} bytes) 但无 '=' 分隔符，清空缓冲区。", _receiveBuffer.Length);
                lock(_lock) _receiveBuffer.Clear();
                return;
            }

            var dataToProcess = currentBufferContent[..lastSeparatorIndex];
            processedLength = lastSeparatorIndex + 1;

            var dataSegments = dataToProcess.Split(['='], StringSplitOptions.RemoveEmptyEntries)
                                .Select(s => s.Trim())
                                .Where(s => !string.IsNullOrEmpty(s))
                                .ToList();

            bool newDataProcessed = false;
            foreach (var segment in dataSegments)
            {
                if (segment.Length < 3)
                {
                    Log.Verbose("无效的重量数据段 (太短): \"{Segment}\"", segment);
                    continue;
                }

                var valuePart = segment.Length >= 6 ? segment.Substring(0, 6) : segment;
                string reversedValue = ReverseWeight(valuePart);
                
                Log.Verbose("解析前的值: Segment='{Segment}', ValuePart='{ValuePart}', Reversed='{ReversedValue}'", segment, valuePart, reversedValue);

                if (double.TryParse(reversedValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var weightInKg))
                {
                    HandleWeightValue(weightInKg, receiveTime); // 使用 receiveTime
                    newDataProcessed = true;
                }
                else
                {
                    Log.Warning("无法解析反转后的重量数据: {ReversedData} (来自 ValuePart: {OriginalValuePart}, Segment: {OriginalSegment})", reversedValue, valuePart, segment);
                }
            }
            if (!newDataProcessed && _lastProcessedWeightData == null && _weightSettings.WeightType == WeightType.Static) {
                 _weightDataSubject.OnNext(null);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "解析重量数据缓冲区时发生错误。内容: {BufferContent}", currentBufferContent);
            lock(_lock) _receiveBuffer.Clear();
            _weightDataSubject.OnNext(null);
        }
        finally
        {
            if (processedLength > 0)
            {
                lock (_lock)
                {
                    if (processedLength <= _receiveBuffer.Length)
                    {
                        _receiveBuffer.Remove(0, processedLength);
                    }
                    else
                    {
                        _receiveBuffer.Clear();
                    }
                }
            }
        }
    }

    private static string ReverseWeight(string weightStr)
    {
        if (string.IsNullOrEmpty(weightStr)) return string.Empty;
        var chars = weightStr.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }

    private void HandleWeightValue(double weightKg, DateTime timestamp)
    {
        // 始终直接创建并发布数据，不再等待静态稳定
        _lastProcessedWeightData = new WeightData { Value = weightKg, Unit = "kg", Timestamp = timestamp };
        
        // 根据称重类型记录不同的日志信息
        if (_weightSettings.WeightType == WeightType.Static)
        {
        }
        else // Dynamic
        {
            Log.Debug("动态重量: {Weight} kg at {Timestamp}", weightKg, timestamp);
        }
        
        _weightDataSubject.OnNext(_lastProcessedWeightData); 
    }

    public Task<WeightData?> GetCurrentWeightAsync()
    {
        return !IsConnected ?
            Task.FromResult<WeightData?>(null) : Task.FromResult(_lastProcessedWeightData);
    }

    public void Dispose()
    {
        Log.Information("正在 Dispose WeightService...");
        Task.Run(async () => await DisconnectAsync()).Wait(TimeSpan.FromSeconds(2));
        _serialPort?.Dispose();
        _weightDataSubject.OnCompleted();
        _weightDataSubject.Dispose();
        GC.SuppressFinalize(this);
    }
} 