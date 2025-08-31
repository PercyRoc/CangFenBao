using System.IO.Ports;
using System.Text;
using DeviceService.DataSourceDevices.Weight;
using Serilog;

// Assuming SerialPortParams is here or accessible

namespace DeviceService.DataSourceDevices.SerialPort;

public class SerialPortService : IDisposable
{
    // 用于保护对 _serialPort 的并发访问，避免在读取时被并发关闭
    private readonly object _serialPortLock = new();

    
    private const int ReconnectInterval = 3000; // 重连间隔，单位毫秒
    private const int MaxReconnectAttempts = 10; // 最大重连次数

    private readonly string _deviceName;
    private readonly SerialPortParams _serialPortParams;
    

    private bool _disposed;
    private bool _autoReconnect = true;
    private bool _isReconnecting;
    private int _reconnectAttempts;
    private Thread? _reconnectThread;
    private System.IO.Ports.SerialPort? _serialPort;

    /// <summary>
    ///     构造函数
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
    ///     获取当前连接状态
    /// </summary>
    public bool IsConnected
    {
        get
        {
            try
            {
                if (_serialPort == null)
                {
                    Log.Debug("设备 {DeviceName} 串口对象为null，视为未连接", _deviceName);
                    return false;
                }

                // 增加句柄有效性检查，防止"Handle is not initialized"错误
                // 通过尝试访问IsOpen属性来间接检查句柄状态
                try
                {
                    var isOpen = _serialPort.IsOpen;
                }
                catch (InvalidOperationException)
                {
                    Log.Debug("设备 {DeviceName} 串口句柄无效，视为未连接", _deviceName);
                    return false;
                }

                return _serialPort.IsOpen;
            }
            catch (ObjectDisposedException ode)
            {
                // 对象已释放，放在最前面因为它是InvalidOperationException的子类
                Log.Debug(ode, "设备 {DeviceName} 检查串口连接状态时对象已释放，视为未连接", _deviceName);
                return false;
            }
            catch (InvalidOperationException ioe)
            {
                // 句柄未初始化是常见问题，记录但不视为致命错误
                Log.Warning(ioe, "设备 {DeviceName} 检查串口连接状态时句柄未初始化，视为未连接", _deviceName);
                return false;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "设备 {DeviceName} 检查串口连接状态时发生错误，视为未连接", _deviceName);
                return false;
            }
        }
    }

    /// <summary>
    ///     当接收到数据时触发
    /// </summary>
    public event Action<byte[]>? DataReceived;

    /// <summary>
    ///     当连接状态改变时触发
    /// </summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>
    ///     释放资源
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     连接到串口设备
    /// </summary>
    /// <returns>如果连接成功则返回 true，否则 false</returns>
    public bool Connect()
    {
        if (_disposed)
        {
            Log.Warning("设备 {DeviceName} SerialPortService 已释放，无法连接", _deviceName);
            return false;
        }

        if (IsConnected)
        {
            Log.Information("设备 {DeviceName} 已连接", _deviceName);
            return true;
        }

        try
        {
            // 先清理之前的连接
            Disconnect();

            _serialPort = new System.IO.Ports.SerialPort
            {
                PortName = _serialPortParams.PortName,
                BaudRate = _serialPortParams.BaudRate,
                DataBits = _serialPortParams.DataBits,
                StopBits = _serialPortParams.StopBits,
                Parity = _serialPortParams.Parity,
                Encoding = Encoding.ASCII,
                ReadTimeout = 2000,
                WriteTimeout = 2000
            };

            lock (_serialPortLock)
            {
                _serialPort.DataReceived += OnDataReceivedHandler;
                _serialPort.Open();
            }

            Log.Information("设备 {DeviceName} 串口 {PortName} 连接成功", _deviceName, _serialPortParams.PortName);

            // 触发连接状态变化事件
            ConnectionChanged?.Invoke(true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "设备 {DeviceName} 连接串口失败", _deviceName);
            _serialPort = null;
            return false;
        }
    }

    /// <summary>
    ///     断开串口连接
    /// </summary>
    public void Disconnect()
    {
        if (_serialPort == null) return;
        lock (_serialPortLock)
        {
            var wasConnected = false;
            try
            {
                wasConnected = _serialPort.IsOpen;
            }
            catch
            {
                // ignore
            }

            try
            {
                if (!_serialPort.IsOpen) return;
                _serialPort.DataReceived -= OnDataReceivedHandler;
                _serialPort.Close();
                Log.Information("设备 {DeviceName} 串口已断开", _deviceName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "设备 {DeviceName} 断开串口时发生错误", _deviceName);
            }
            finally
            {
                try { _serialPort?.Dispose(); }
                catch
                {
                    // ignored
                }

                _serialPort = null;

                // 如果之前是连接状态，现在断开，触发事件
                if (wasConnected)
                {
                    ConnectionChanged?.Invoke(false);
                }
            }
        }
    }

    /// <summary>
    ///     发送数据
    /// </summary>
    /// <param name="data">要发送的字节数组</param>
    public bool Send(byte[] data)
    {
        if (!IsConnected || data.Length == 0)
        {
            Log.Warning("设备 {DeviceName} 未连接或无数据可发送", _deviceName);
            return false;
        }

        try
        {
            if (_serialPort == null)
            {
                Log.Warning("设备 {DeviceName} 串口对象为null，无法发送数据", _deviceName);
                return false;
            }

            // 在发送前再次检查连接状态，避免并发关闭
            if (!_serialPort.IsOpen)
            {
                Log.Warning("设备 {DeviceName} 在发送前发现串口已关闭", _deviceName);
                return false;
            }

            _serialPort.Write(data, 0, data.Length);
            Log.Debug("设备 {DeviceName} 发送 {Length} 字节数据", _deviceName, data.Length);
            return true;
        }
        catch (InvalidOperationException ioe)
        {
            Log.Error(ioe, "设备 {DeviceName} 发送数据时句柄未初始化", _deviceName);
            StartReconnectIfNeeded();
            return false;
        }
        catch (TimeoutException timeoutEx)
        {
            Log.Warning(timeoutEx, "设备 {DeviceName} 发送数据超时", _deviceName);
            StartReconnectIfNeeded();
            return false;
        }
        catch (IOException ioEx)
        {
            Log.Error(ioEx, "设备 {DeviceName} 发送数据时发生IO异常", _deviceName);
            StartReconnectIfNeeded();
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "设备 {DeviceName} 发送数据失败", _deviceName);
            return false;
        }
    }

    /// <summary>
    ///     发送字符串数据
    /// </summary>
    /// <param name="text">要发送的字符串</param>
    public bool Send(string text)
    {
        if (!IsConnected || string.IsNullOrEmpty(text))
        {
            Log.Warning("设备 {DeviceName} 未连接或无字符串可发送", _deviceName);
            return false;
        }

        try
        {
            if (_serialPort == null)
            {
                Log.Warning("设备 {DeviceName} 串口对象为null，无法发送字符串", _deviceName);
                return false;
            }

            // 在发送前再次检查连接状态，避免并发关闭
            if (!_serialPort.IsOpen)
            {
                Log.Warning("设备 {DeviceName} 在发送前发现串口已关闭", _deviceName);
                return false;
            }

            _serialPort.Write(text);
            Log.Debug("设备 {DeviceName} 发送字符串: {Text}", _deviceName,
                text.Length > 50 ? string.Concat(text.AsSpan(0, 50), "...") : text);
            return true;
        }
        catch (InvalidOperationException ioe)
        {
            Log.Error(ioe, "设备 {DeviceName} 发送字符串时句柄未初始化", _deviceName);
            StartReconnectIfNeeded();
            return false;
        }
        catch (TimeoutException timeoutEx)
        {
            Log.Warning(timeoutEx, "设备 {DeviceName} 发送字符串超时", _deviceName);
            StartReconnectIfNeeded();
            return false;
        }
        catch (IOException ioEx)
        {
            Log.Error(ioEx, "设备 {DeviceName} 发送字符串时发生IO异常", _deviceName);
            StartReconnectIfNeeded();
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "设备 {DeviceName} 发送字符串失败", _deviceName);
            return false;
        }
    }


    /// <summary>
    ///     串口数据接收事件处理（重构后更健壮）
    /// </summary>
    private void OnDataReceivedHandler(object sender, SerialDataReceivedEventArgs e)
    {
        // 创建一个局部的串口引用，以防止在操作期间 _serialPort 字段被其他线程置为 null
        System.IO.Ports.SerialPort? port;
        lock (_serialPortLock)
        {
            port = _serialPort;
            // 增加双重检查，防止句柄未初始化的错误
            if (port == null)
            {
                Log.Debug("设备 {DeviceName} 串口对象为null，跳过数据接收处理", _deviceName);
                return;
            }

            // 通过尝试访问IsOpen属性来间接检查句柄状态
            try
            {
                var isOpen = port.IsOpen;
            }
            catch (InvalidOperationException)
            {
                Log.Debug("设备 {DeviceName} 串口句柄无效，跳过数据接收处理", _deviceName);
                return;
            }
        }

        // 如果在我们检查时，端口已经是 null 或关闭状态，则直接返回
        if (port == null || !port.IsOpen)
        {
            return;
        }

        try
        {
            // 现在，后续操作都只使用这个局部变量 'port'
            var bytesToRead = port.BytesToRead;
            if (bytesToRead <= 0)
            {
                return;
            }

            // 使用一个新的缓冲区，而不是共享的 _readBuffer，以简化线程安全问题
            var buffer = new byte[bytesToRead];
            var bytesRead = port.Read(buffer, 0, buffer.Length);

            if (bytesRead > 0)
            {
                // 为事件调用创建一个数据的精确副本，确保数据不会被后续的读取覆盖
                var receivedData = new byte[bytesRead];
                Array.Copy(buffer, 0, receivedData, 0, bytesRead);
                
                Log.Debug("设备 {DeviceName} 收到 {Length} 字节数据 (线程Id={ThreadId})", _deviceName, bytesRead, Thread.CurrentThread.ManagedThreadId);
                
                // 触发数据接收事件
                DataReceived?.Invoke(receivedData);
            }
        }
        catch (InvalidOperationException ioe)
        {
            // 当端口在检查 IsOpen 之后但在 Read 之前被关闭时，这是最可能发生的异常
            Log.Warning(ioe, "设备 {DeviceName} 在读取数据时发生操作无效错误（可能已被关闭）", _deviceName);
            StartReconnectIfNeeded();
        }
        catch (IOException ioEx)
        {
            // 当设备被物理拔出时，可能会发生IO异常
            Log.Error(ioEx, "设备 {DeviceName} 读取数据时发生IO异常（设备可能已断开）", _deviceName);
            StartReconnectIfNeeded();
        }
        catch (Exception ex)
        {
            // 捕获其他所有意外错误
            Log.Error(ex, "设备 {DeviceName} 在 OnDataReceivedHandler 中发生未知错误", _deviceName);
            StartReconnectIfNeeded();
        }
    }

    /// <summary>
    ///     启动重连（如果需要且允许）
    /// </summary>
    private void StartReconnectIfNeeded()
    {
        if (!_autoReconnect || _disposed || _isReconnecting)
            return;

        // 断开当前连接
        try { Disconnect(); }
        catch { /* ignored */ }

        // 启动重连线程
        StartReconnectThread();
    }

    /// <summary>
    ///     启动重连线程
    /// </summary>
    private void StartReconnectThread()
    {
        if (_reconnectThread?.IsAlive == true)
        {
            Log.Debug("设备 {DeviceName} 已有重连线程在运行，跳过", _deviceName);
            return;
        }

        // 先等待一段时间，确保前一个连接完全清理完毕
        Thread.Sleep(1000);

        _reconnectThread = new Thread(() =>
        {
            Log.Information("启动设备 {DeviceName} 的自动重连任务", _deviceName);

            _isReconnecting = true;
            _reconnectAttempts = 0;

            try
            {
                while (!_disposed && !IsConnected && _autoReconnect && _reconnectAttempts < MaxReconnectAttempts)
                {
                    _reconnectAttempts++;
                    Log.Information("设备 {DeviceName} 尝试重连 (第 {Attempt} 次)", _deviceName, _reconnectAttempts);

                    try
                    {
                        // 尝试重新连接
                        var connected = Connect();
                        if (connected)
                        {
                            Log.Information("设备 {DeviceName} 重连成功", _deviceName);
                            return; // 连接成功，退出重连循环
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "设备 {DeviceName} 重连失败", _deviceName);
                    }

                    // 等待后重试
                    if (_reconnectAttempts >= MaxReconnectAttempts) continue;
                    var delayMs = Math.Min(ReconnectInterval * _reconnectAttempts, 30000); // 最大等待30秒
                    Log.Debug("设备 {DeviceName} 重连失败，等待 {Delay}ms 后重试", _deviceName, delayMs);
                    Thread.Sleep(delayMs);
                }

                if (_reconnectAttempts >= MaxReconnectAttempts)
                {
                    Log.Error("设备 {DeviceName} 重连次数达到上限 ({MaxAttempts})，停止重连", _deviceName, MaxReconnectAttempts);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "设备 {DeviceName} 重连线程执行时发生未处理的异常", _deviceName);
            }
            finally
            {
                _isReconnecting = false;
                Log.Debug("设备 {DeviceName} 重连线程结束", _deviceName);
            }
        })
        {
            IsBackground = true, // 设置为后台线程，确保应用程序关闭时自动终止
            Name = $"SerialPortReconnect_{_deviceName}", // 设置线程名称，便于调试
            Priority = ThreadPriority.BelowNormal // 设置较低优先级，避免影响主线程
        };

        _reconnectThread.Start();
        Log.Debug("设备 {DeviceName} 重连线程已启动 (线程ID: {ThreadId})", _deviceName, _reconnectThread.ManagedThreadId);
    }

    /// <summary>
    ///     释放资源
    /// </summary>
    private void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            // 停止自动重连
            _autoReconnect = false;

            // 等待重连线程结束
            var currentReconnectThread = _reconnectThread;
            if (currentReconnectThread != null)
            {
                Log.Debug("设备 {DeviceName} 等待重连线程结束...", _deviceName);
                if (!currentReconnectThread.Join(5000)) // 等待最多5秒
                {
                    Log.Warning("设备 {DeviceName} 等待重连线程结束超时", _deviceName);
                }
            }

            Disconnect();
        }

        _disposed = true;
    }
}