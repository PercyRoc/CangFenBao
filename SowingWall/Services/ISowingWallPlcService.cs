namespace SowingWall.Services
{
    /// <summary>
    /// 播种墙PLC服务接口
    /// </summary>
    public interface ISowingWallPlcService
    {
        /// <summary>
        /// 获取PLC是否已连接。
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// 异步连接到PLC。
        /// </summary>
        /// <param name="timeoutMs">连接超时时间（毫秒）。</param>
        /// <returns>如果连接成功则为 true，否则为 false。</returns>
        Task<bool> ConnectAsync(int timeoutMs = 5000);

        /// <summary>
        /// 异步断开与PLC的连接。
        /// </summary>
        Task DisconnectAsync();

        /// <summary>
        /// 异步读取保持寄存器。
        /// </summary>
        /// <param name="startAddress">起始地址。</param>
        /// <param name="quantity">要读取的寄存器数量。</param>
        /// <param name="timeoutSeconds">操作超时时间（秒）。</param>
        /// <returns>读取到的寄存器值数组。</returns>
        Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort quantity, int timeoutSeconds = 5);

        /// <summary>
        /// 异步写入单个寄存器。
        /// </summary>
        /// <param name="registerAddress">寄存器地址。</param>
        /// <param name="value">要写入的值。</param>
        /// <param name="timeoutSeconds">操作超时时间（秒）。</param>
        Task WriteSingleRegisterAsync(ushort registerAddress, ushort value, int timeoutSeconds = 5);
    }
} 