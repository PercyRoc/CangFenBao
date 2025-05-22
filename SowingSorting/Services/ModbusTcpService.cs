using EasyModbus;
using Serilog;
using SowingSorting.Models.Settings;

namespace SowingSorting.Services
{
    /// <summary>
    /// Modbus TCP 服务实现
    /// </summary>
    public class ModbusTcpService : IModbusTcpService
    {
        private ModbusClient? _modbusClient;
        private ModbusTcpSettings? _currentConnectionSettings; // 存储当前连接的配置

        public event ConnectionStatusChangedEventHandler? ConnectionStatusChanged;

        /// <summary>
        /// 获取当前是否已连接
        /// </summary>
        public bool IsConnected => _modbusClient is { Connected: true };

        /// <summary>
        /// 获取当前连接的设置信息 (如果已连接)
        /// </summary>
        public ModbusTcpSettings? ConnectedSettings => _currentConnectionSettings;

        /// <summary>
        /// 异步连接到 Modbus TCP 服务器
        /// </summary>
        public Task<bool> ConnectAsync(ModbusTcpSettings? settings)
        {
            return settings == null
                ? throw new ArgumentNullException(nameof(settings))
                : Task.Run(() =>
            {
                try
                {
                    if (_modbusClient is { Connected: true })
                    {
                        Log.Information("Modbus TCP 客户端已连接 (到 {Ip}:{Port})，将先断开。", _currentConnectionSettings!.IpAddress, _currentConnectionSettings?.Port);
                        _modbusClient.Disconnect();
                        _currentConnectionSettings = null;
                    }

                    _modbusClient = new ModbusClient(settings.IpAddress, settings.Port)
                    {
                        ConnectionTimeout = settings.ConnectionTimeout,
                    };

                    // EasyModbus 的 Connect 方法不是异步的，所以用 Task.Run 包裹

                    _modbusClient.Connect();
                    _currentConnectionSettings = settings; // 保存成功的连接配置
                    Log.Information("Modbus TCP 客户端连接成功: IP={IpAddress}, Port={Port}, UnitId=1",

                                    settings.IpAddress, settings.Port);

                    // 触发连接成功事件
                    OnConnectionStatusChanged(true);

                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Modbus TCP 客户端连接失败: IP={IpAddress}, Port={Port}, UnitId=1",

                                settings.IpAddress, settings.Port);
                    _modbusClient!.Disconnect(); // 确保失败时断开
                    _modbusClient = null;
                    _currentConnectionSettings = null;
                    return false;
                }
            });
        }

        /// <summary>
        /// 异步断开 Modbus TCP 连接
        /// </summary>
        public Task DisconnectAsync()
        {
            return Task.Run(() =>
            {
                if (_modbusClient is { Connected: true })
                {
                    try
                    {
                        var ip = _currentConnectionSettings?.IpAddress;
                        var port = _currentConnectionSettings?.Port;
                        _modbusClient.Disconnect();
                        _currentConnectionSettings = null;
                        _modbusClient = null; // 释放 ModbusClient 实例
                        Log.Information("Modbus TCP 客户端已从 {Ip}:{Port} 断开。", ip, port);

                        // 触发断开连接事件
                        OnConnectionStatusChanged(false);
                    }
                    catch (Exception? ex)
                    {
                        Log.Error(ex, "断开 Modbus TCP 客户端时发生错误。配置: {Settings}", _currentConnectionSettings);
                    }
                }
                else
                {
                    Log.Information("Modbus TCP 客户端未连接或已为 null，无需断开操作。");
                }
            });
        }

        /// <summary>
        /// 异步读取保持寄存器
        /// </summary>
        public Task<int[]> ReadHoldingRegistersAsync(int startingAddress, int quantity)
        {
            return Task.Run(() =>
            {
                if (!IsConnected || _modbusClient == null)
                {
                    Log.Warning("Modbus TCP 客户端未连接，无法读取保持寄存器。地址: {Address}, 数量: {Quantity}", startingAddress, quantity);
                    throw new InvalidOperationException("Modbus client is not connected or has been disposed.");
                }
                try
                {
                    // EasyModbus ReadHoldingRegisters 的 startingAddress 是 0-based
                    var values = _modbusClient.ReadHoldingRegisters(startingAddress, quantity);
                    Log.Debug("从地址 {StartingAddress} 读取 {Quantity} 个保持寄存器成功。读取自 {Ip}:{Port}", 
                                startingAddress, quantity, _currentConnectionSettings?.IpAddress, _currentConnectionSettings!.Port);
                    return values;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "读取保持寄存器失败。地址: {Address}, 数量: {Quantity}。连接信息: {Settings}", 
                                startingAddress, quantity, _currentConnectionSettings);
                    throw; // 重新抛出异常，让调用者处理
                }
            });
        }

        /// <summary>
        /// 异步写入单个寄存器
        /// </summary>
        public Task<bool> WriteSingleRegisterAsync(int startingAddress, int value)
        {
            return Task.Run(() =>
            {
                if (!IsConnected || _modbusClient == null)
                {
                    Log.Warning("Modbus TCP 客户端未连接，无法写入单个寄存器。地址: {Address}, 值: {Value}", startingAddress, value);
                    throw new InvalidOperationException("Modbus client is not connected or has been disposed.");
                }
                try
                {
                    // EasyModbus WriteSingleRegister 的 startingAddress 是 0-based
                    _modbusClient.WriteSingleRegister(startingAddress, value);
                    Log.Debug("向地址 {StartingAddress} 写入值 {Value} 成功。写入到 {Ip}:{Port}", 
                                startingAddress, value, _currentConnectionSettings?.IpAddress, _currentConnectionSettings!.Port);
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "写入单个寄存器失败。地址: {Address}, 值: {Value}。连接信息: {Settings}", 
                                startingAddress, value, _currentConnectionSettings);
                    return false; // 或重新抛出异常
                }
            });
        }

        /// <summary>
        /// 触发连接状态变化事件
        /// </summary>
        /// <param name="isConnected">是否已连接</param>
        protected virtual void OnConnectionStatusChanged(bool isConnected)
        {
            ConnectionStatusChanged?.Invoke(this, isConnected);
        }
    }
} 