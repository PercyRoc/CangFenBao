using System.IO.Ports;
using Serilog;

namespace BenFly.Services.Belt;

/// <summary>
///     皮带串口服务实现
/// </summary>
internal class BeltSerialService : IBeltSerialService
{
    private readonly object _lock = new();
    private bool _isDisposed;
    private SerialPort? _serialPort;

    /// <inheritdoc />
    public bool IsOpen
    {
        get
        {
            lock (_lock)
            {
                return _serialPort?.IsOpen ?? false;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<bool>? ConnectionStatusChanged;

    /// <inheritdoc />
    public event EventHandler<byte[]>? DataReceived;

    /// <inheritdoc />
    public void Open(SerialPort settings)
    {
        lock (_lock)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(BeltSerialService));

            try
            {
                // 如果已经打开，先关闭
                Close();

                _serialPort = new SerialPort
                {
                    PortName = settings.PortName,
                    BaudRate = settings.BaudRate,
                    DataBits = settings.DataBits,
                    Parity = settings.Parity,
                    StopBits = settings.StopBits
                };

                // 注册数据接收事件
                _serialPort.DataReceived += SerialPortOnDataReceived;

                try
                {
                    _serialPort.Open();
                }
                catch (UnauthorizedAccessException ex)
                {
                    Log.Error(ex, "无法访问串口 {PortName}，可能是权限不足或串口被占用", settings.PortName);
                    
                    // 检查串口是否被其他程序占用
                    var processes = System.Diagnostics.Process.GetProcesses()
                        .Where(p => 
                        {
                            try
                            {
                                using var port = new SerialPort(settings.PortName);
                                port.Open();
                                port.Close();
                                return false;
                            }
                            catch
                            {
                                return true;
                            }
                        });

                    if (processes.Any())
                    {
                        Log.Warning("串口 {PortName} 可能被以下进程占用: {Processes}", 
                            settings.PortName, 
                            string.Join(", ", processes.Select(p => $"{p.ProcessName}({p.Id})")));
                    }

                    throw new InvalidOperationException($"无法访问串口 {settings.PortName}，请检查权限或是否被其他程序占用", ex);
                }

                Log.Information("串口 {PortName} 已打开", settings.PortName);

                // 触发状态变更事件
                ConnectionStatusChanged?.Invoke(this, true);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "打开串口 {PortName} 时发生错误", settings.PortName);
                throw;
            }
        }
    }

    /// <inheritdoc />
    public void Close()
    {
        lock (_lock)
        {
            if (_serialPort?.IsOpen != true) return;
            try
            {
                _serialPort.DataReceived -= SerialPortOnDataReceived;
                _serialPort.Close();
                _serialPort.Dispose();
                _serialPort = null;
                Log.Information("串口已关闭");

                // 触发状态变更事件
                ConnectionStatusChanged?.Invoke(this, false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "关闭串口时发生错误");
            }
        }
    }

    /// <inheritdoc />
    public void SendData(byte[] data)
    {
        lock (_lock)
        {
            if (!_isDisposed)
            {
                if (_serialPort?.IsOpen != true)
                    throw new InvalidOperationException("串口未打开");

                try
                {
                    _serialPort.Write(data, 0, data.Length);
                    Log.Debug("发送数据: {Data}", BitConverter.ToString(data));
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "发送数据时发生错误");
                    throw;
                }
            }
            else
            {
                throw new ObjectDisposedException(nameof(BeltSerialService));
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed) return;

        lock (_lock)
        {
            if (_isDisposed) return;

            Close();
            _isDisposed = true;
        }

        GC.SuppressFinalize(this);
    }

    private void SerialPortOnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_serialPort is not { IsOpen: true }) return;

        try
        {
            // 读取接收缓冲区中的字节数
            var bytesToRead = _serialPort.BytesToRead;
            if (bytesToRead <= 0) return;

            // 创建缓冲区
            var buffer = new byte[bytesToRead];

            // 读取数据
            _serialPort.Read(buffer, 0, bytesToRead);

            Log.Debug("接收数据: {Data}", BitConverter.ToString(buffer));

            // 触发数据接收事件
            DataReceived?.Invoke(this, buffer);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "接收数据时发生错误");
        }
    }
}