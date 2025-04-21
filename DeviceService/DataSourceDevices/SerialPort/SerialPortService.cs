using System.IO.Ports;
using System.Text;
using DeviceService.DataSourceDevices.Weight; // Assuming SerialPortParams is here or accessible
using Serilog;

namespace DeviceService.DataSourceDevices.SerialPort;

/// <summary>
/// 通用串口通信服务
/// </summary>
public class SerialPortService : IDisposable
{
    private const int ReadBufferSize = 8192;
    private const int MaxSendChunkSize = 4096; // 发送数据分块大小
    private const int MaxReadLoopIterations = 100; // 防止无限循环
    private readonly string _deviceName;
    private readonly object _lock = new();
    private readonly byte[] _readBuffer = new byte[ReadBufferSize];
    private readonly CancellationTokenSource _cts = new();

    private bool _disposed;
    private bool _isConnected;
    private System.IO.Ports.SerialPort? _serialPort;
    private readonly SerialPortParams _serialPortParams;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="deviceName">设备名称 (用于日志)</param>
    /// <param name="portParams">串口配置参数</param>
    public SerialPortService(string deviceName, SerialPortParams portParams)
    {
        _deviceName = deviceName;
        _serialPortParams = portParams ?? throw new ArgumentNullException(nameof(portParams));
        Log.Debug("设备 {DeviceName} 创建 SerialPortService", _deviceName);
    }

    /// <summary>
    /// 当接收到数据时触发
    /// </summary>
    public event Action<byte[]>? DataReceived;

    /// <summary>
    /// 当连接状态改变时触发
    /// </summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>
    /// 获取当前连接状态
    /// </summary>
    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (_isConnected == value) return;
            _isConnected = value;
            Log.Information("设备 {DeviceName} 连接状态变更为: {IsConnected}", _deviceName, _isConnected);
            // 在状态实际改变后触发事件
            // 使用防御性复制避免在回调中修改导致的问题
            var handler = ConnectionChanged;
            handler?.Invoke(_isConnected);
        }
    }

    /// <summary>
    /// 连接到串口设备
    /// </summary>
    /// <returns>如果启动连接过程成功则返回 true，否则 false</returns>
    public bool Connect()
    {
        lock (_lock)
        {
            if (_isConnected)
            {
                Log.Information("设备 {DeviceName} 已连接，无需重复连接", _deviceName);
                return true;
            }

            if (_disposed)
            {
                Log.Warning("设备 {DeviceName} SerialPortService 已释放，无法连接", _deviceName);
                return false;
            }

            Log.Information("设备 {DeviceName} 正在尝试连接串口...", _deviceName);

            // 1. 验证配置
            if (string.IsNullOrEmpty(_serialPortParams.PortName))
            {
                Log.Error("设备 {DeviceName} 串口名称未配置", _deviceName);
                IsConnected = false; // 确保状态为 false
                return false;
            }

            // 2. 检查串口是否存在
            try
            {
                var availablePorts = System.IO.Ports.SerialPort.GetPortNames();
                if (!availablePorts.Contains(_serialPortParams.PortName))
                {
                    Log.Error("设备 {DeviceName} 配置的串口 {PortName} 不存在", _deviceName, _serialPortParams.PortName);
                    IsConnected = false; // 确保状态为 false
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "设备 {DeviceName} 检查可用串口列表时出错", _deviceName);
                IsConnected = false;
                return false;
            }


            // 3. 尝试关闭现有实例 (如果存在)
            DisconnectInternal(false); // 内部调用，不触发外部 ConnectionChanged

            try
            {
                Log.Debug(
                    "设备 {DeviceName} 正在创建串口实例，参数：端口={Port}, 波特率={BaudRate}, 数据位={DataBits}, 停止位={StopBits}, 校验位={Parity}",
                    _deviceName,
                    _serialPortParams.PortName,
                    _serialPortParams.BaudRate,
                    _serialPortParams.DataBits,
                    _serialPortParams.StopBits,
                    _serialPortParams.Parity);

                _serialPort = new System.IO.Ports.SerialPort
                {
                    PortName = _serialPortParams.PortName,
                    BaudRate = _serialPortParams.BaudRate,
                    DataBits = _serialPortParams.DataBits,
                    StopBits = _serialPortParams.StopBits,
                    Parity = _serialPortParams.Parity,
                    Encoding = Encoding.ASCII, // 默认 ASCII，可考虑配置
                    ReadBufferSize = ReadBufferSize * 2, // 稍微增大
                    ReadTimeout = 500, // 可考虑配置
                    WriteTimeout = 500 // 可考虑配置
                };

                // 检查串口是否已被占用
                if (IsPortInUse(_serialPortParams.PortName))
                {
                    Log.Error("设备 {DeviceName} 串口 {PortName} 已被其他程序占用", _deviceName, _serialPortParams.PortName);
                    _serialPort.Dispose();
                    _serialPort = null;
                    IsConnected = false; // 确保状态为 false
                    return false;
                }

                _serialPort.DataReceived += OnDataReceivedHandler;
                _serialPort.ErrorReceived += OnErrorReceivedHandler;

                Log.Information("设备 {DeviceName} 正在打开串口 {PortName}...", _deviceName, _serialPort.PortName);
                _serialPort.Open();
                // 检查是否真的打开成功
                if (!_serialPort.IsOpen)
                {
                    Log.Error("设备 {DeviceName} 打开串口 {PortName} 失败，IsOpen 为 false", _deviceName, _serialPort.PortName);
                    _serialPort.DataReceived -= OnDataReceivedHandler;
                    _serialPort.ErrorReceived -= OnErrorReceivedHandler;
                    _serialPort.Dispose();
                    _serialPort = null;
                    IsConnected = false; // 确保状态为 false
                    return false;
                }

                // ** 重要：只有在成功打开后才更新 IsConnected 并触发事件 **
                IsConnected = true;
                Log.Information("设备 {DeviceName} 串口 {PortName} 已成功连接", _deviceName, _serialPort.PortName);
                return true; // 连接成功
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Error(ex, "设备 {DeviceName} 无权限访问串口 {PortName}", _deviceName, _serialPortParams.PortName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "设备 {DeviceName} 打开串口 {PortName} 时发生错误", _deviceName, _serialPortParams.PortName);
            }

            // 如果捕获到异常，清理资源并确保状态为 false
            DisconnectInternal(false);
            IsConnected = false; // 确保状态为 false
            return false; // 连接失败
        }
    }

    /// <summary>
    /// 断开串口连接
    /// </summary>
    public void Disconnect()
    {
        lock (_lock)
        {
            DisconnectInternal(true); // 外部调用，触发 ConnectionChanged
        }
    }

    /// <summary>
    /// 内部断开连接逻辑
    /// </summary>
    /// <param name="triggerEvent">是否触发 ConnectionChanged 事件</param>
    private void DisconnectInternal(bool triggerEvent)
    {
        // 这个方法应该在 lock (_lock) 内部调用

        if (_serialPort == null && !IsConnected) // 如果已经断开，则无需操作
        {
            Log.Debug("设备 {DeviceName} 已处于断开状态，无需重复断开", _deviceName);
            return;
        }

        Log.Information("设备 {DeviceName} 正在断开串口连接...", _deviceName);

        // 如果triggerEvent为false，暂时保存事件处理器引用并清空
        // 这样IsConnected改变时不会触发事件
        Action<bool>? savedHandler = null;
        if (!triggerEvent)
        {
            savedHandler = ConnectionChanged;
            ConnectionChanged = null;
        }

        // 标记为断开，停止数据处理
        IsConnected = false;

        // 如果暂时清空了事件处理器，现在恢复它
        if (!triggerEvent && savedHandler != null)
        {
            ConnectionChanged = savedHandler;
        }

        // 通知所有后台任务停止
        try
        {
            _cts.Cancel(); // 请求停止所有使用此标记的异步操作
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "设备 {DeviceName} 取消异步操作时发生异常", _deviceName);
        }

        var portToClose = _serialPort;
        _serialPort = null; // 立即释放引用

        if (portToClose != null)
        {
            try
            {
                // 先移除事件处理器
                portToClose.DataReceived -= OnDataReceivedHandler;
                portToClose.ErrorReceived -= OnErrorReceivedHandler;

                if (portToClose.IsOpen)
                {
                    Log.Debug("设备 {DeviceName} 正在关闭串口 {PortName}...", _deviceName, portToClose.PortName);

                    // 简化为直接同步关闭，避免使用Task.Run
                    try
                    {
                        // 清空缓冲区可能有助于更快关闭
                        try
                        {
                            portToClose.DiscardInBuffer();
                        }
                        catch (Exception)
                        {
                            /* ignore */
                        }

                        try
                        {
                            portToClose.DiscardOutBuffer();
                        }
                        catch (Exception)
                        {
                            /* ignore */
                        }

                        // 直接关闭串口
                        portToClose.Close();
                        Log.Debug("设备 {DeviceName} 串口 {PortName} 已关闭", _deviceName, portToClose.PortName);
                    }
                    catch (TimeoutException)
                    {
                        Log.Warning("设备 {DeviceName} 关闭串口 {PortName} 超时", _deviceName, portToClose.PortName);
                        // 超时后，Dispose 应该会尝试再次关闭
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "设备 {DeviceName} 关闭串口时发生异常", _deviceName);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "设备 {DeviceName} 断开串口连接时发生错误", _deviceName);
            }
            finally
            {
                try
                {
                    portToClose.Dispose(); // 确保 Dispose 被调用
                    Log.Debug("设备 {DeviceName} 串口实例已 Dispose", _deviceName);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "设备 {DeviceName} Dispose 串口实例时发生异常", _deviceName);
                }
            }
        }

        Log.Information("设备 {DeviceName} 串口连接已断开", _deviceName);
    }

    /// <summary>
    /// 发送数据
    /// </summary>
    /// <param name="data">要发送的字节数组</param>
    public bool Send(byte[] data)
    {
        if (data.Length == 0)
        {
            Log.Warning("设备 {DeviceName} 尝试发送空数据", _deviceName);
            return false;
        }

        lock (_lock)
        {
            if (!IsConnected || _serialPort == null || !_serialPort.IsOpen)
            {
                Log.Error("设备 {DeviceName} 未连接，无法发送数据", _deviceName);
                return false;
            }

            try
            {
                Log.Debug("设备 {DeviceName} 正在发送 {Length} 字节数据: {DataHex}", _deviceName, data.Length,
                    BitConverter.ToString(data.Length > 50 ? data.Take(50).ToArray() : data)); // 限制日志长度

                // 对大数据包进行分块发送，避免阻塞
                if (data.Length > MaxSendChunkSize)
                {
                    Log.Debug("设备 {DeviceName} 数据长度超过 {MaxSize} 字节，将分块发送", _deviceName, MaxSendChunkSize);
                    int offset = 0;
                    while (offset < data.Length)
                    {
                        int chunkSize = Math.Min(MaxSendChunkSize, data.Length - offset);
                        _serialPort.Write(data, offset, chunkSize);
                        offset += chunkSize;
                        Log.Verbose("设备 {DeviceName} 已发送 {Offset}/{Total} 字节", _deviceName, offset, data.Length);
                    }
                }
                else
                {
                    _serialPort.Write(data, 0, data.Length);
                }

                Log.Debug("设备 {DeviceName} 数据发送完成", _deviceName);
                return true;
            }
            catch (TimeoutException ex)
            {
                Log.Error(ex, "设备 {DeviceName} 发送数据超时", _deviceName);
                // 发送超时可能意味着连接问题
                DisconnectInternal(true); // 触发断开事件
                return false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "设备 {DeviceName} 发送数据时发生错误", _deviceName);
                // 其他发送错误也可能意味着连接问题
                DisconnectInternal(true); // 触发断开事件
                return false;
            }
        }
    }

    /// <summary>
    /// 发送字符串数据 (使用串口的默认编码)
    /// </summary>
    /// <param name="text">要发送的字符串</param>
    public bool Send(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            Log.Warning("设备 {DeviceName} 尝试发送空字符串", _deviceName);
            return false;
        }

        lock (_lock)
        {
            if (!IsConnected || _serialPort == null || !_serialPort.IsOpen)
            {
                Log.Error("设备 {DeviceName} 未连接，无法发送字符串", _deviceName);
                return false;
            }

            try
            {
                // 对长字符串只记录前50个字符避免日志过大
                string logText = text.Length > 50 ? text.Substring(0, 50) + "..." : text;
                Log.Debug("设备 {DeviceName} 正在发送字符串: {Text}", _deviceName, logText);

                // 对长字符串进行分块发送
                if (text.Length > MaxSendChunkSize / 2) // 考虑到字符编码可能导致字节数增加
                {
                    int chunkSize = MaxSendChunkSize / 2;
                    for (int i = 0; i < text.Length; i += chunkSize)
                    {
                        string chunk = text.Substring(i, Math.Min(chunkSize, text.Length - i));
                        _serialPort.Write(chunk);
                    }
                }
                else
                {
                    _serialPort.Write(text);
                }

                Log.Debug("设备 {DeviceName} 字符串发送完成", _deviceName);
                return true;
            }
            catch (TimeoutException ex)
            {
                Log.Error(ex, "设备 {DeviceName} 发送字符串超时", _deviceName);
                DisconnectInternal(true);
                return false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "设备 {DeviceName} 发送字符串时发生错误", _deviceName);
                DisconnectInternal(true);
                return false;
            }
        }
    }


    /// <summary>
    /// 串口数据接收事件处理
    /// </summary>
    private void OnDataReceivedHandler(object sender, SerialDataReceivedEventArgs e)
    {
        // 获取一个标识，标记当前接收处理是否应继续
        CancellationToken cancellationToken;
        lock (_lock)
        {
            cancellationToken = _cts.Token;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            Log.Debug("设备 {DeviceName} 接收处理已被取消", _deviceName);
            return;
        }

        System.IO.Ports.SerialPort? currentPort;
        bool currentlyConnected;

        lock (_lock) // 检查状态和获取引用时加锁
        {
            currentPort = _serialPort;
            currentlyConnected = IsConnected;
        }

        if (currentPort == null || !currentlyConnected)
        {
            Log.Debug("设备 {DeviceName} 在 OnDataReceivedHandler 中检测到端口未连接，忽略数据", _deviceName);
            return; // 端口已关闭或未连接
        }

        // 验证端口是否打开，这需要单独检查，因为IsOpen可能会抛出异常
        bool isPortOpen;
        try
        {
            isPortOpen = currentPort.IsOpen;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "设备 {DeviceName} 检查端口IsOpen时发生异常", _deviceName);
            DisconnectSafely();
            return;
        }

        if (!isPortOpen)
        {
            Log.Debug("设备 {DeviceName} 在 OnDataReceivedHandler 中检测到端口已关闭，忽略数据", _deviceName);
            DisconnectSafely();
            return;
        }

        try
        {
            int bytesToRead;
            try
            {
                bytesToRead = currentPort.BytesToRead;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "设备 {DeviceName} 获取BytesToRead时发生异常", _deviceName);
                DisconnectSafely();
                return;
            }

            if (bytesToRead <= 0)
            {
                Log.Verbose("设备 {DeviceName} OnDataReceivedHandler 触发但无数据可读", _deviceName);
                return; // 没有数据可读
            }

            Log.Verbose("设备 {DeviceName} 准备读取 {BytesToRead} 字节数据", _deviceName, bytesToRead);

            // 读取数据，避免一次性读取过多，循环读取直到读完或缓冲区满
            var totalBytesRead = 0;
            var receivedDataList = new List<byte>(); // 存储本次事件接收的所有数据
            var loopCount = 0;

            while (totalBytesRead < bytesToRead && !cancellationToken.IsCancellationRequested && loopCount < MaxReadLoopIterations)
            {
                loopCount++;

                // 每次循环都重新检查端口状态，增强安全性
                bool isStillOpen;
                try
                {
                    lock (_lock)
                    {
                        // 重新获取引用，防止在循环中端口被释放
                        currentPort = _serialPort;
                        if (currentPort == null) break;
                        isStillOpen = currentPort.IsOpen;
                    }
                }
                catch
                {
                    break; // 发生异常就退出循环
                }

                if (!isStillOpen) break;

                int bytesAvailable;
                try
                {
                    bytesAvailable = currentPort.BytesToRead;
                }
                catch
                {
                    break; // 如果获取可读字节数失败，退出循环
                }

                if (bytesAvailable == 0) break; // 没有更多数据了

                var bytesToReadNow = Math.Min(bytesAvailable, _readBuffer.Length); // 每次最多读缓冲区大小
                try
                {
                    var bytesRead = currentPort.Read(_readBuffer, 0, bytesToReadNow);
                    if (bytesRead > 0)
                    {
                        // 将读取到的数据添加到列表
                        var newData = new byte[bytesRead];
                        Array.Copy(_readBuffer, 0, newData, 0, bytesRead);
                        receivedDataList.AddRange(newData);
                        totalBytesRead += bytesRead;
                        Log.Verbose("设备 {DeviceName} 本次读取 {BytesRead} 字节，总计 {TotalBytesRead}", _deviceName, bytesRead,
                            totalBytesRead);
                    }
                    else
                    {
                        // Read 返回 0 通常表示流结束，但对于串口可能不常见
                        Log.Warning("设备 {DeviceName} SerialPort.Read 返回 0 字节", _deviceName);
                        break;
                    }
                }
                catch (TimeoutException)
                {
                    // 读取超时，可能数据还没完全到达，暂时退出，等待下次事件
                    Log.Warning("设备 {DeviceName} 读取串口数据超时", _deviceName);
                    break; // 退出循环，保留已读数据
                }
                catch (InvalidOperationException ioe)
                {
                    // 端口可能在此期间关闭
                    Log.Warning(ioe, "设备 {DeviceName} 读取串口时发生 InvalidOperationException (端口可能已关闭)", _deviceName);
                    DisconnectSafely();
                    return; // 退出处理
                }
                catch (Exception readEx) // 捕获读取过程中的其他异常
                {
                    Log.Error(readEx, "设备 {DeviceName} 读取串口数据时发生异常", _deviceName);
                    DisconnectSafely();
                    return; // 退出处理
                }

                // 如果到达最大迭代次数，记录警告
                if (loopCount >= MaxReadLoopIterations)
                {
                    Log.Warning("设备 {DeviceName} 读取循环达到最大迭代次数 {MaxCount}，强制退出循环", _deviceName, MaxReadLoopIterations);
                }
            }

            if (receivedDataList.Count > 0)
            {
                var receivedBytes = receivedDataList.ToArray();
                
                // 触发外部事件，使用防御性复制
                var handler = DataReceived;
                if (handler == null) return;
                try
                {
                    handler(receivedBytes);
                }
                catch (Exception invokeEx)
                {
                    Log.Error(invokeEx, "设备 {DeviceName} 调用 DataReceived 事件处理程序时发生异常", _deviceName);
                }
            }
            else
            {
                Log.Debug("设备 {DeviceName} OnDataReceivedHandler 完成，但未读取到有效数据", _deviceName);
            }
        }
        catch (Exception ex) // 捕获 BytesToRead 或其他外部操作的异常
        {
            Log.Error(ex, "设备 {DeviceName} 在 OnDataReceivedHandler 中发生未预期的错误", _deviceName);
            DisconnectSafely();
        }
    }

    // 安全断开连接的辅助方法，避免重复代码
    private void DisconnectSafely()
    {
        try
        {
            lock (_lock)
            {
                DisconnectInternal(true);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "设备 {DeviceName} 安全断开连接时发生异常", _deviceName);
        }
    }

    /// <summary>
    /// 串口错误事件处理
    /// </summary>
    private void OnErrorReceivedHandler(object sender, SerialErrorReceivedEventArgs e)
    {
        string portName;
        lock (_lock)
        {
            portName = _serialPort?.PortName ?? "Unknown";
        }

        Log.Warning("设备 {DeviceName} 串口发生错误: Type={ErrorType}, Port={PortName}", _deviceName, e.EventType, portName);

        switch (e.EventType)
        {
            case SerialError.Frame:
            case SerialError.Overrun:
            case SerialError.RXParity:
                // 这些是数据传输错误，通常不致命，记录警告
                break;

            case SerialError.RXOver:
                // 接收缓冲区溢出，可能需要清空缓冲区
                Log.Warning("设备 {DeviceName} 串口接收缓冲区溢出 (RXOver)，尝试清空输入缓冲区", _deviceName);
                lock (_lock)
                {
                    try
                    {
                        if (_serialPort?.IsOpen == true)
                        {
                            _serialPort.DiscardInBuffer();
                            Log.Debug("设备 {DeviceName} 输入缓冲区已清空", _deviceName);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "设备 {DeviceName} 清空串口输入缓冲区时出错", _deviceName);
                    }
                }

                break;

            case SerialError.TXFull:
                // 发送缓冲区满，通常是临时问题
                Log.Warning("设备 {DeviceName} 串口发送缓冲区已满 (TXFull)", _deviceName);
                break;

            default:
                // 其他未知或严重错误，可能需要断开连接
                Log.Error("设备 {DeviceName} 串口发生未知或严重错误: {ErrorType}，将断开连接", _deviceName, e.EventType);
                lock (_lock)
                {
                    DisconnectInternal(true);
                }

                break;
        }
    }

    /// <summary>
    /// 检查串口是否被其他程序占用
    /// </summary>
    /// <param name="portName">串口名称</param>
    /// <returns>如果被占用则返回 true</returns>
    private static bool IsPortInUse(string portName)
    {
        try
        {
            // 尝试打开和关闭端口来检查是否可用
            using var port = new System.IO.Ports.SerialPort(portName);
            port.Open();
            port.Close();
            return false; // 能成功打开和关闭，说明未被占用
        }
        catch (UnauthorizedAccessException)
        {
            // 捕获此异常表示端口已被占用
            return true;
        }
        catch (IOException ioEx)
        {
            // IO 异常也可能表示端口不可用或不存在
            Log.Warning(ioEx, "检查串口 {PortName} 是否被占用时发生 IO 异常", portName);
            return true; // 谨慎起见，认为它不可用
        }
        catch (Exception ex)
        {
            // 其他异常，可能表示端口不存在或配置错误
            Log.Error(ex, "检查串口 {PortName} 是否被占用时发生未知异常", portName);
            return true; // 发生未知异常，也认为不可用
        }
    }


    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 实际的资源释放逻辑
    /// </summary>
    private void Dispose(bool disposing)
    {
        if (_disposed) return;

        Log.Debug("设备 {DeviceName} 开始 Dispose SerialPortService...", _deviceName);

        if (disposing)
        {
            try
            {
                // 首先安全断开连接
                lock (_lock)
                {
                    // 先标记为已断开，防止重复断开
                    if (!_disposed)
                    {
                        DisconnectInternal(false); // 内部断开，不触发事件
                    }
                }

                // 然后释放 CancellationTokenSource
                try 
                {
                    _cts.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "设备 {DeviceName} 释放 CancellationTokenSource 时发生异常", _deviceName);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "设备 {DeviceName} Dispose 过程中发生异常", _deviceName);
            }
        }

        _disposed = true;
        Log.Information("设备 {DeviceName} SerialPortService 已 Dispose", _deviceName);
    }
}