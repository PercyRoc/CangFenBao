using DeviceService.DataSourceDevices.SerialPort;
using Serilog;

namespace DeviceService.DataSourceDevices.Belt;

/// <summary>
/// 皮带控制串口服务
/// </summary>
public class BeltSerialService : IDisposable
{
    private readonly SerialPortService _serialPortService;
    private readonly BeltSerialParams _beltParams; // 添加字段存储参数
    private bool _disposed;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="serialPortParams">串口参数</param>
    public BeltSerialService(BeltSerialParams serialPortParams) // 确认参数类型
    {
        _serialPortService = new SerialPortService("皮带控制", serialPortParams);
        _beltParams = serialPortParams; // 存储参数
        // 订阅连接状态事件
        _serialPortService.ConnectionChanged += OnConnectionChanged;
    }

    /// <summary>
    /// 连接状态变更事件
    /// </summary>
    public event Action<bool>? ConnectionStatusChanged;

    /// <summary>
    /// 是否已连接
    /// </summary>
    public bool IsOpen => _serialPortService.IsConnected;

    /// <summary>
    /// 启动连接
    /// </summary>
    /// <returns>连接是否成功</returns>
    public bool Connect()
    {
        return _serialPortService.Connect();
    }

    /// <summary>
    /// 断开连接
    /// </summary>
    public void Disconnect()
    {
        _serialPortService.Disconnect();
    }

    /// <summary>
    /// 发送数据
    /// </summary>
    /// <param name="data">要发送的数据</param>
    /// <returns>发送是否成功</returns>
    public bool SendData(byte[] data)
    {
        return _serialPortService.Send(data);
    }

    /// <summary>
    /// 发送命令文本
    /// </summary>
    /// <param name="command">要发送的命令</param>
    /// <returns>发送是否成功</returns>
    public bool SendCommand(string command)
    {
        return _serialPortService.Send(command);
    }

    /// <summary>
    /// 发送启动皮带命令
    /// </summary>
    /// <returns>发送是否成功</returns>
    public bool StartBelt()
    {
        Log.Debug("皮带控制 - 发送启动命令: {Command}", _beltParams.StartCommand);
        return SendCommand(_beltParams.StartCommand);
    }

    /// <summary>
    /// 发送停止皮带命令
    /// </summary>
    /// <returns>发送是否成功</returns>
    public bool StopBelt()
    {
        Log.Debug("皮带控制 - 发送停止命令: {Command}", _beltParams.StopCommand);
        return SendCommand(_beltParams.StopCommand);
    }

    /// <summary>
    /// 连接状态变更处理
    /// </summary>
    private void OnConnectionChanged(bool isConnected)
    {
        ConnectionStatusChanged?.Invoke(isConnected);
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _serialPortService.Dispose();
            _disposed = true;
        }
    }
} 