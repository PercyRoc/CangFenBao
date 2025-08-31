using System.Globalization;
using System.IO.Ports;
using System.Text;
using Common.Services.Settings;
using Serilog;

namespace DeviceService.DataSourceDevices.Weight;

/// <summary>
///     串口重量称服务 (直接使用 System.IO.Ports.SerialPort)
/// </summary>
public class SerialPortWeightService(ISettingsService settingsService) : IDisposable
{
    private const int MaxQueueSize = 50; // 重量队列最大长度
    private readonly List<byte> _receiveBuffer = [];
    private readonly Queue<(double Weight, DateTime Timestamp)> _weightQueue = new();

    private System.IO.Ports.SerialPort? _serialPort;
    private bool _disposed;
    private bool _isConnected;

    public bool IsConnected
    {
        get => _isConnected && _serialPort is { IsOpen: true };
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

    public bool Start()
    {
        try
        {
            Log.Information("正在启动串口重量称服务...");

            // 如果已经连接，先断开
            if (_serialPort != null)
            {
                if (_serialPort.IsOpen)
                {
                    _serialPort.DataReceived -= HandleDataReceived;
                    _serialPort.Close();
                }
                _serialPort.Dispose();
                _serialPort = null;
            }

            // 获取配置
            var weightSettings = settingsService.LoadSettings<WeightSettings>();

            // 创建新的SerialPort实例
            _serialPort = new System.IO.Ports.SerialPort
            {
                PortName = weightSettings.SerialPortParams.PortName,
                BaudRate = weightSettings.SerialPortParams.BaudRate,
                DataBits = weightSettings.SerialPortParams.DataBits,
                StopBits = weightSettings.SerialPortParams.StopBits,
                Parity = weightSettings.SerialPortParams.Parity,
                Encoding = Encoding.ASCII,
                ReadTimeout = 2000,
                WriteTimeout = 2000
            };

            // 注册数据接收事件
            _serialPort.DataReceived += HandleDataReceived;

            // 打开串口
            _serialPort.Open();

            IsConnected = true;
            Log.Information("串口重量称服务启动成功，端口: {PortName}", weightSettings.SerialPortParams.PortName);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动串口重量称服务时发生异常");
            IsConnected = false;

            // 清理资源
            if (_serialPort == null) return false;
            try
            {
                _serialPort.DataReceived -= HandleDataReceived;
                if (_serialPort.IsOpen)
                {
                    _serialPort.Close();
                }
                _serialPort.Dispose();
            }
            catch
            {
                // 忽略清理过程中的异常
            }
            _serialPort = null;

            return false;
        }
    }

    public void Stop()
    {
        try
        {
            // 断开串口连接
            if (_serialPort != null)
            {
                if (_serialPort.IsOpen)
                {
                    _serialPort.DataReceived -= HandleDataReceived;
                    _serialPort.Close();
                }
                _serialPort.Dispose();
                _serialPort = null;
            }

            // 清空数据
            _receiveBuffer.Clear();
            _weightQueue.Clear();

            IsConnected = false;
            Log.Information("串口重量称服务已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止串口重量称服务时发生错误");
        }
    }



    private void HandleDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            // 检查串口状态
            if (_serialPort is not { IsOpen: true })
            {
                Log.Warning("串口不可用，忽略接收到的数据");
                return;
            }

            // 读取所有可用数据
            var bytesToRead = _serialPort.BytesToRead;
            if (bytesToRead <= 0) return;

            var data = new byte[bytesToRead];
            var bytesRead = _serialPort.Read(data, 0, data.Length);

            if (bytesRead <= 0) return;

            // 记录原始接收数据用于调试
            var hexData = BitConverter.ToString(data, 0, bytesRead).Replace("-", " ");
            Log.Debug("WeightScale 接收到串口数据: 长度={Length}, 十六进制={HexData}", bytesRead, hexData);

            try
            {
                // 尝试转换为ASCII字符串（用于调试）
                string asciiData;
                try
                {
                    asciiData = Encoding.ASCII.GetString(data, 0, bytesRead);
                    Log.Debug("WeightScale ASCII数据: \"{AsciiData}\"", asciiData);
                }
                catch (Exception asciiEx)
                {
                    asciiData = $"转换失败: {asciiEx.Message}";
                    Log.Warning(asciiEx, "WeightScale ASCII转换失败，但继续处理二进制数据");
                }

                _receiveBuffer.AddRange(data.Take(bytesRead));
                Log.Debug("WeightScale 缓冲区状态: 总长度={BufferLength}, 新增数据长度={NewDataLength}",
                    _receiveBuffer.Count, bytesRead);

                const int maxInternalBufferSize = 4096; // 定义一个合适的缓冲区大小
                if (_receiveBuffer.Count > maxInternalBufferSize)
                {
                    Log.Warning("WeightScale 内部接收缓冲区超过限制 ({Length} > {Limit})，清空缓冲区", _receiveBuffer.Count,
                        maxInternalBufferSize);
                    _receiveBuffer.Clear();
                    return;
                }

                // 记录缓冲区内容用于调试
                var bufferHex = BitConverter.ToString([.. _receiveBuffer]).Replace("-", " ");
                Log.Debug("WeightScale 当前缓冲区内容: {BufferContent}", bufferHex);

                // 处理缓冲区数据
                try
                {
                    ProcessBuffer(DateTime.Now);
                }
                catch (Exception processEx)
                {
                    Log.Error(processEx, "WeightScale 处理缓冲区数据时发生错误，原始数据: {HexData}, ASCII: {AsciiData}",
                        hexData, asciiData);
                    _receiveBuffer.Clear();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "WeightScale 处理接收到的串口数据时发生严重错误，原始数据: {HexData}, 数据长度: {DataLength}",
                    hexData, bytesRead);

                _receiveBuffer.Clear();
            }
        }
        catch (Exception ex)
        {
            try
            {
                Log.Fatal(ex, "HandleDataReceived 最终兜底捕获到异常");
            }
            catch
            {
                // ignored
            }

            try
            {
                _receiveBuffer.Clear();
            }
            catch
            {
                // ignored
            }
        }
    }

    /// <summary>
    ///     处理内部缓冲区的重量数据 (需要调整以使用 StringBuilder)
    /// </summary>
    private void ProcessBuffer(DateTime receiveTime)
    {
        try
        {
            // 只处理静态称重协议
            Log.Debug("WeightScale 开始处理缓冲区数据");
            ProcessEqualsProtocol(receiveTime);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "WeightScale 处理缓冲区数据时发生错误，缓冲区长度: {BufferLength}",
                _receiveBuffer.Count);

            // 记录缓冲区内容用于调试
            try
            {
                var bufferHex = BitConverter.ToString([.. _receiveBuffer]).Replace("-", " ");
                Log.Error("WeightScale 错误时的缓冲区内容: {BufferContent}", bufferHex);
            }
            catch (Exception logEx)
            {
                Log.Error(logEx, "WeightScale 记录缓冲区内容时发生错误");
            }
        }
    }



    /// <summary>
    ///     处理=协议数据（仅用于静态称重）
    /// </summary>
    private void ProcessEqualsProtocol(DateTime receiveTime)
    {
        try
        {
            // 将字节转换为字符串进行=协议处理
            string bufferContent;
            try
            {
                bufferContent = Encoding.ASCII.GetString([.. _receiveBuffer]);
            }
            catch (Exception encodingEx)
            {
                Log.Error(encodingEx, "WeightScale =协议: ASCII编码转换失败，缓冲区长度: {BufferLength}", _receiveBuffer.Count);
                var bufferHex = BitConverter.ToString([.. _receiveBuffer]).Replace("-", " ");
                Log.Error("WeightScale =协议: 编码失败时的缓冲区内容: {BufferContent}", bufferHex);
                _receiveBuffer.Clear();
                return;
            }

            if (string.IsNullOrEmpty(bufferContent))
            {
                Log.Debug("WeightScale =协议: 缓冲区为空，跳过=协议处理");
                return;
            }

            Log.Debug("WeightScale =协议: 开始处理，缓冲区内容: \"{Content}\", 长度: {Length}",
                bufferContent, bufferContent.Length);

            var processedLength = 0;
            var processedSegments = 0;

            try
            {
                var lastSeparatorIndex = bufferContent.LastIndexOf('=');

                if (lastSeparatorIndex == -1)
                {
                    Log.Debug("WeightScale =协议: 缓冲区中未找到分隔符 '='，数据不符合=协议格式");
                    if (_receiveBuffer.Count <= 100)
                    {
                        Log.Debug("WeightScale =协议: 缓冲区长度 {Length} <= 100，继续等待更多数据", _receiveBuffer.Count);
                        return;
                    }

                    Log.Warning("WeightScale =协议: 缓冲区过长但无分隔符，可能数据格式错误，清空缓冲区");
                    _receiveBuffer.Clear();
                    return;
                }

                var dataToProcess = bufferContent[..lastSeparatorIndex];
                processedLength = lastSeparatorIndex + 1;

                Log.Verbose("WeightScale =协议: 准备处理的数据段: {DataSegment}", dataToProcess);

                var dataSegments = dataToProcess.Split(['='], StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();

                Log.Debug("WeightScale =协议: 找到 {SegmentCount} 个数据段待处理", dataSegments.Count);

                foreach (var segment in dataSegments)
                {
                    try
                    {
                        if (segment.Length < 3)
                        {
                            Log.Warning("WeightScale =协议: 无效的重量数据段 (太短): \"{Segment}\"", segment);
                            continue;
                        }

                        var valuePart = segment.Length >= 6 ? segment[..6] : segment;
                        var reversedValue = ReverseWeight(valuePart);
                        Log.Verbose("WeightScale =协议: 解析反转后的值: {ReversedValue} (来自段: {Segment})", reversedValue,
                            valuePart);

                        if (float.TryParse(reversedValue, NumberStyles.Float, CultureInfo.InvariantCulture,
                                out var weightInKg))
                        {
                            var weightG = weightInKg * 1000;
                            Log.Debug("WeightScale =协议: 成功解析重量 {WeightKg}kg = {WeightG}g", weightInKg, weightG);
                            ProcessStaticWeight(weightG, receiveTime);
                            processedSegments++;
                        }
                        else
                        {
                            Log.Warning("WeightScale =协议: 无法解析反转后的重量数据: {ReversedData} (原始段: {OriginalSegment})",
                                reversedValue, valuePart);
                        }
                    }
                    catch (Exception segmentEx)
                    {
                        Log.Error(segmentEx, "WeightScale =协议: 处理单个数据段时发生错误，段内容: {Segment}", segment);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "WeightScale =协议: 解析重量数据缓冲区时发生错误，缓冲区内容: {BufferContent}", bufferContent);
                _receiveBuffer.Clear();
            }
            finally
            {
                if (processedLength > 0 && processedLength <= _receiveBuffer.Count)
                {
                    Log.Debug("WeightScale =协议: 从缓冲区移除已处理的 {ProcessedLength} 个字节", processedLength);
                    _receiveBuffer.RemoveRange(0, processedLength);
                    Log.Debug("WeightScale =协议: 处理后剩余缓冲区内容: \"{RemainingBuffer}\"",
                        BitConverter.ToString([.. _receiveBuffer]).Replace("-", " "));
                }
                else if (processedLength > _receiveBuffer.Count)
                {
                    Log.Warning("WeightScale =协议: 计算的处理长度 {ProcessedLength} 大于缓冲区长度 {BufferLength}，清空缓冲区",
                        processedLength, _receiveBuffer.Count);
                    _receiveBuffer.Clear();
                }

                // 总结数据处理结果
                Log.Debug("WeightScale =协议: 数据处理完成 - 处理了 {ProcessedSegments} 个数据段，当前重量队列数量: {QueueCount}",
                    processedSegments, _weightQueue.Count);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "WeightScale =协议处理过程中发生严重错误，缓冲区长度: {BufferLength}", _receiveBuffer.Count);

            // 记录详细的缓冲区内容
            try
            {
                var bufferHex = BitConverter.ToString([.. _receiveBuffer]).Replace("-", " ");
                Log.Error("WeightScale =协议错误时的缓冲区内容: {BufferContent}", bufferHex);
            }
            catch (Exception logEx)
            {
                Log.Error(logEx, "WeightScale =协议记录缓冲区内容时发生错误");
            }

            _receiveBuffer.Clear();
        }
    }

    private void ProcessStaticWeight(double weightG, DateTime timestamp)
    {
        // 直接将重量数据添加到队列，不进行稳定性检查
        if (weightG <= 0)
        {
            Log.Debug("忽略零重量或负重量: {Weight:F2}g", weightG);
            return;
        }

        // 检查队列长度，防止内存泄漏 (最大50个数据)
        if (_weightQueue.Count >= MaxQueueSize)
        {
            _weightQueue.Dequeue(); // 移除最旧的数据
            Log.Debug("重量队列已满 (50)，移除最旧数据");
        }

        _weightQueue.Enqueue((weightG, timestamp));
        Log.Debug("添加重量数据: {Weight:F2}g (队列长度: {Count})", weightG, _weightQueue.Count);
    }





    private static string ReverseWeight(string weightStr)
    {
        var chars = weightStr.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }


    /// <summary>
    ///     获取当前最新的重量数据
    /// </summary>
    /// <returns>最新的重量值（克），如果无有效数据则返回null</returns>
    public double? GetLatestWeight()
    {
        if (_weightQueue.Count <= 0) return null;
        var latestWeight = _weightQueue.Last();
        // 检查重量是否有效（大于0且在合理范围内）
        if (latestWeight.Weight is <= 0 or >= 50000) return null; // 最大50kg
        Log.Debug("获取到最新重量: {Weight:F2}g", latestWeight.Weight);
        return latestWeight.Weight;
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            Stop();
        }

        _disposed = true;
    }
}