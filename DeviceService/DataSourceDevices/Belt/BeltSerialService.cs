using Common.Services.Settings;
using DeviceService.DataSourceDevices.SerialPort;
using Serilog;

namespace DeviceService.DataSourceDevices.Belt;

/// <summary>
///     皮带控制串口服务
/// </summary>
public class BeltSerialService : IDisposable
{
    private readonly BeltSerialParams _beltParams; // 添加字段存储参数
    private readonly SerialPortService _serialPortService;
    private readonly ISettingsService _settingsService;
    private bool _disposed;

    /// <summary>
    ///     构造函数
    /// </summary>
    /// <param name="settingsService">配置服务</param>
    public BeltSerialService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        // 从配置中加载串口参数
        _beltParams = _settingsService.LoadSettings<BeltSerialParams>();
        Log.Debug("皮带控制 - 从配置加载串口参数: PortName={PortName}, BaudRate={BaudRate}",
            _beltParams.PortName, _beltParams.BaudRate);

        _serialPortService = new SerialPortService("皮带控制", _beltParams);
        // 订阅连接状态事件
        _serialPortService.ConnectionChanged += OnConnectionChanged;

        // 构造函数中立即尝试连接串口
        try
        {
            var connected = Connect();
            Log.Information("皮带控制串口初始化连接 {Status}", connected ? "成功" : "失败");
            // 确保UI状态更新
            ConnectionStatusChanged?.Invoke(_serialPortService.IsConnected);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "皮带控制串口初始化连接时发生错误");
        }
    }

    /// <summary>
    ///     是否已连接
    /// </summary>
    public bool IsOpen => _serialPortService.IsConnected;

    /// <summary>
    ///     释放资源
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _serialPortService.Dispose();
            _disposed = true;
        }
    }

    /// <summary>
    ///     连接状态变更事件
    /// </summary>
    public event Action<bool>? ConnectionStatusChanged;

    /// <summary>
    ///     启动连接
    /// </summary>
    /// <returns>连接是否成功</returns>
    private bool Connect()
    {
        // 重新加载最新的配置参数
        var latestParams = _settingsService.LoadSettings<BeltSerialParams>();
        // 如果配置参数有变化，需要更新串口参数
        if (latestParams.PortName != _beltParams.PortName ||
            latestParams.BaudRate != _beltParams.BaudRate ||
            latestParams.DataBits != _beltParams.DataBits ||
            latestParams.StopBits != _beltParams.StopBits ||
            latestParams.Parity != _beltParams.Parity)
        {
            Log.Information("皮带控制 - 串口参数已变化，重新配置串口: {OldPort} -> {NewPort}",
                _beltParams.PortName, latestParams.PortName);

            // 更新本地参数
            _beltParams.PortName = latestParams.PortName;
            _beltParams.BaudRate = latestParams.BaudRate;
            _beltParams.DataBits = latestParams.DataBits;
            _beltParams.StopBits = latestParams.StopBits;
            _beltParams.Parity = latestParams.Parity;

            // 如果已经连接，先断开
            if (_serialPortService.IsConnected) _serialPortService.Disconnect();
        }

        // 启动命令和停止命令也需要更新
        _beltParams.StartCommand = latestParams.StartCommand;
        _beltParams.StopCommand = latestParams.StopCommand;

        return _serialPortService.Connect();
    }

    /// <summary>
    ///     断开连接
    /// </summary>
    public void Disconnect()
    {
        _serialPortService.Disconnect();
    }

    /// <summary>
    ///     发送数据
    /// </summary>
    /// <param name="data">要发送的数据</param>
    /// <returns>发送是否成功</returns>
    public bool SendData(byte[] data)
    {
        // 确保串口已连接
        if (!IsOpen)
        {
            Log.Debug("皮带控制 - 发送数据前检测到串口未连接，尝试连接");
            if (!Connect())
            {
                Log.Error("皮带控制 - 发送数据前连接串口失败");
                return false;
            }
        }

        return _serialPortService.Send(data);
    }

    /// <summary>
    ///     发送命令文本
    /// </summary>
    /// <param name="command">要发送的命令</param>
    /// <returns>发送是否成功</returns>
    public bool SendCommand(string command)
    {
        // 确保串口已连接
        if (!IsOpen)
        {
            Log.Debug("皮带控制 - 发送命令前检测到串口未连接，尝试连接");
            if (!Connect())
            {
                Log.Error("皮带控制 - 发送命令前连接串口失败");
                return false;
            }
        }

        return _serialPortService.Send(command);
    }

    /// <summary>
    ///     发送启动皮带命令
    /// </summary>
    /// <returns>发送是否成功</returns>
    public bool StartBelt()
    {
        // 确保串口已连接
        if (!IsOpen)
        {
            Log.Information("皮带控制 - 启动皮带前检测到串口未连接，尝试连接");
            if (!Connect())
            {
                Log.Error("皮带控制 - 启动皮带前连接串口失败");
                return false;
            }

            Log.Information("皮带控制 - 启动皮带前连接串口成功");
        }

        // 重新获取最新的启动命令
        var latestParams = _settingsService.LoadSettings<BeltSerialParams>();
        _beltParams.StartCommand = latestParams.StartCommand;

        Log.Debug("皮带控制 - 发送启动命令: {Command}", _beltParams.StartCommand);
        return SendCommand(_beltParams.StartCommand);
    }

    /// <summary>
    ///     发送停止皮带命令
    /// </summary>
    /// <returns>发送是否成功</returns>
    public bool StopBelt()
    {
        // 确保串口已连接
        if (!IsOpen)
        {
            Log.Information("皮带控制 - 停止皮带前检测到串口未连接，尝试连接");
            if (!Connect())
            {
                Log.Error("皮带控制 - 停止皮带前连接串口失败");
                return false;
            }

            Log.Information("皮带控制 - 停止皮带前连接串口成功");
        }

        // 重新获取最新的停止命令
        var latestParams = _settingsService.LoadSettings<BeltSerialParams>();
        _beltParams.StopCommand = latestParams.StopCommand;

        Log.Debug("皮带控制 - 发送停止命令: {Command}", _beltParams.StopCommand);
        return SendCommand(_beltParams.StopCommand);
    }

    /// <summary>
    ///     连接状态变更处理
    /// </summary>
    private void OnConnectionChanged(bool isConnected)
    {
        // 添加日志记录连接状态变化
        Log.Information("皮带控制串口连接状态变更: {Status}", isConnected ? "已连接" : "已断开");

        try
        {
            // 触发事件
            ConnectionStatusChanged?.Invoke(isConnected);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "皮带控制 - 触发ConnectionStatusChanged事件时发生异常");
        }
    }
}