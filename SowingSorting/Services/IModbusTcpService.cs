using SowingSorting.Models.Settings;

namespace SowingSorting.Services
{
    public delegate void ConnectionStatusChangedEventHandler(object sender, bool isConnected);

    /// <summary>
    /// Modbus TCP 服务接口
    /// </summary>
    public interface IModbusTcpService
    {
        /// <summary>
        /// Modbus TCP 连接状态变化事件
        /// </summary>
        event ConnectionStatusChangedEventHandler ConnectionStatusChanged;

        /// <summary>
        /// 获取当前是否已连接
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// 获取当前连接的设置信息 (如果已连接)
        /// </summary>
        ModbusTcpSettings? ConnectedSettings { get; }

        /// <summary>
        /// 异步连接到 Modbus TCP 服务器
        /// </summary>
        /// <param name="settings">连接设置</param>
        /// <returns>如果连接成功则为 true，否则为 false</returns>
        Task<bool> ConnectAsync(ModbusTcpSettings? settings);

        /// <summary>
        /// 异步断开 Modbus TCP 连接
        /// </summary>
        Task DisconnectAsync();

        /// <summary>
        /// 异步读取保持寄存器
        /// </summary>
        /// <param name="startingAddress">起始地址 (0-based)</param>
        /// <param name="quantity">读取数量</param>
        /// <returns>读取到的寄存器值数组</returns>
        Task<int[]> ReadHoldingRegistersAsync(int startingAddress, int quantity);

        /// <summary>
        /// 异步写入单个寄存器
        /// </summary>
        /// <param name="startingAddress">起始地址 (0-based)</param>
        /// <param name="value">要写入的值</param>
        /// <returns>如果写入成功则为 true，否则为 false</returns>
        Task<bool> WriteSingleRegisterAsync(int startingAddress, int value);

        // TODO: 根据需要添加更多 Modbus 读写方法 (例如线圈、离散输入、输入寄存器、写入多个寄存器等)
    }
} 