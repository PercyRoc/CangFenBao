using Common.Services.Settings;
using DeviceService.DataSourceDevices.TCP;
using Serilog;
using SowingWall.Models.Settings;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SowingWall.Services
{
    /// <summary>
    /// 播种墙PLC服务，用于通过Modbus TCP与PLC通信。
    /// </summary>
    public class SowingWallPlcService : ISowingWallPlcService, IDisposable
    {
        private readonly ISettingsService _settingsService;
        private TcpClientService? _tcpClientService;
        private ModbusTcpSettings _settings = new(); // 使用默认值初始化

        // 存储待处理的Modbus请求，键为事务ID，值为TaskCompletionSource
        private readonly ConcurrentDictionary<ushort, TaskCompletionSource<byte[]>> _pendingRequests = new();
        private ushort _nextTransactionId = 0; // 下一个可用的事务ID
        private readonly object _transactionIdLock = new(); // 用于保护事务ID生成的锁
        private readonly CancellationTokenSource _serviceCts = new(); // 服务级别的取消令牌源

        private volatile bool _isConnected; // PLC是否已连接的易失性标志
        private volatile bool _isConnecting; // 是否正在连接中的易失性标志
        private readonly SemaphoreSlim _connectSemaphore = new(1, 1); //确保一次只有一个连接操作

        /// <summary>
        /// 获取PLC是否已连接。
        /// </summary>
        public bool IsConnected => _isConnected && _tcpClientService?.IsConnected() == true;

        public SowingWallPlcService(ISettingsService settingsService)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            LoadSettings();
        }

        private void LoadSettings()
        {
            Log.Debug("播种墙PLC服务: 正在加载ModbusTcpSettings...");
            _settings = _settingsService.LoadSettings<ModbusTcpSettings>();
            Log.Information("播种墙PLC服务: 配置已加载 (IP={Ip}, 端口={Port}, 从站ID={SlaveId})", 
                            _settings.PlcIpAddress, _settings.PlcPort, _settings.SlaveId);
        }

        /// <summary>
        /// 异步连接到PLC。
        /// </summary>
        /// <param name="timeoutMs">连接超时时间（毫秒）。</param>
        /// <returns>如果连接成功则为 true，否则为 false。</returns>
        public async Task<bool> ConnectAsync(int timeoutMs = 5000)
        {
            if (IsConnected) // Optimized initial check
            {
                // Log.Debug("播种墙PLC服务: 已连接，跳过连接尝试。"); // Can be too verbose
                return true;
            }
            if (_isConnecting) // If a connection process is already running by another call.
            {
                Log.Debug("播种墙PLC服务: 连接操作已在进行中，等待其完成。");
                // Simple busy wait for the _isConnecting flag to clear or timeout.
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                // Cap wait time to prevent indefinite loop if _isConnecting flag is stuck
                while(_isConnecting && stopwatch.ElapsedMilliseconds < Math.Max(timeoutMs, 1000) && !_serviceCts.IsCancellationRequested) 
                { 
                    try
                    {
                        await Task.Delay(50, _serviceCts.Token); 
                    }
                    catch (OperationCanceledException) { break; } // Exit if service is shutting down
                }
                stopwatch.Stop();
                return IsConnected; // Return the status after waiting
            }

            await _connectSemaphore.WaitAsync(_serviceCts.Token); 
            try
            {
                // Re-check connection status after acquiring semaphore, as another call might have completed connection.
                if (IsConnected)
                {
                    return true;
                }
                
                _isConnecting = true; // Set flag to indicate this thread is handling the connection attempt.

                // Load/Refresh settings right before attempting to use them for connection.
                // This ensures that this specific connection attempt uses the latest known configuration.
                LoadSettings(); 

                Log.Information("播种墙PLC服务: 尝试连接到PLC {Ip}:{Port}...", _settings.PlcIpAddress, _settings.PlcPort);
                
                // 如果存在旧实例，则释放它
                _tcpClientService?.Dispose(); 

                if (string.IsNullOrEmpty(_settings.PlcIpAddress) || _settings.PlcPort == 0)
                {
                    Log.Warning("播种墙PLC服务: PLC IP地址或端口未配置，无法连接。");
                    _isConnected = false;
                    // _isConnecting will be reset in finally block
                    return false;
                }

                // 创建并连接TcpClientService
                _tcpClientService = new TcpClientService(
                    "SowingWallPLC", // 设备名称
                    _settings.PlcIpAddress, 
                    _settings.PlcPort,
                    HandlePlcDataReceived, // 数据接收回调
                    HandlePlcConnectionStatus, // 连接状态回调
                    true // 在TcpClientService中启用自动重连
                );

                // TcpClientService.Connect 在提供的代码中是同步的, 
                // 但理想情况下应该是异步的或在任务中运行。
                // 为安全起见，我们将其包装起来。
                await Task.Run(() => _tcpClientService.Connect(timeoutMs), _serviceCts.Token);
                
                // HandlePlcConnectionStatus 回调将更新 _isConnected
                if (!IsConnected)
                {
                     Log.Warning("播种墙PLC服务: 连接尝试完成，但服务未连接。");
                }
                return IsConnected;
            }
            catch (OperationCanceledException) when (_serviceCts.IsCancellationRequested)
            {
                Log.Information("播种墙PLC服务: 连接尝试因服务关闭而被取消。");
                _isConnected = false;
                return false;
            }
            catch (OperationCanceledException) // Catch specific timeout from Task.Delay or other cancellations
            {
                Log.Information("播种墙PLC服务: 连接尝试已取消。");
                _isConnected = false;
                return false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "播种墙PLC服务: 连接尝试期间发生错误。");
                _isConnected = false;
                _tcpClientService?.Dispose(); // 确保在错误时清理
                _tcpClientService = null;
                return false;
            }
            finally
            {   
                _isConnecting = false; // Reset flag when operation is complete or failed
                _connectSemaphore.Release(); // 释放信号量
            }
        }

        /// <summary>
        /// 异步断开与PLC的连接。
        /// </summary>
        public Task DisconnectAsync()
        {
            Log.Information("播种墙PLC服务: 正在断开连接...");
            _isConnected = false; // 立即标记为已断开
            _tcpClientService?.Dispose(); // Dispose将处理关闭连接和线程
            _tcpClientService = null;
            FailAllPendingRequests(new OperationCanceledException("用户断开连接。"));
            return Task.CompletedTask;
        }

        // 处理PLC连接状态变更
        private void HandlePlcConnectionStatus(bool connected)
        {
            if (_isConnected == connected) return; // 状态未改变

            _isConnected = connected;
            Log.Information("播种墙PLC服务: PLC连接状态更改为 {Status}", connected ? "已连接" : "已断开");
            
            if (!connected)
            {
                // 如果断开连接，则使所有待处理的请求失败
                FailAllPendingRequests(new Exception("PLC连接丢失。"));
            }
            // 注意: TcpClientService本身处理自动重连
        }

        // 处理从PLC接收到的数据
        private void HandlePlcDataReceived(byte[] frame)
        {
            if (frame == null || frame.Length < 8) // MBAP头(7字节) + 功能码(1字节) 是最小长度
            {
                Log.Warning("播种墙PLC服务: 收到无效或过短的数据帧 (长度: {Length})", frame?.Length ?? 0);
                return;
            }

            try
            {
                ushort transactionId = (ushort)((frame[0] << 8) | frame[1]); // 事务ID
                ushort protocolId = (ushort)((frame[2] << 8) | frame[3]);    // 协议ID (应为0)
                ushort length = (ushort)((frame[4] << 8) | frame[5]);        // 长度 (后续字节数)
                byte unitId = frame[6];                                     // 单元ID (从站地址)

                if (protocolId != 0)
                {
                    Log.Warning("播种墙PLC服务: 收到包含无效Modbus协议ID的数据帧: {ProtocolId}", protocolId);
                    return;
                }

                // 可选: 如果需要，检查单元ID，尽管在TCP中严格由网关管理
                // if (unitId != _settings.SlaveId)
                // {
                //     Log.Warning("播种墙PLC服务: 收到针对意外单元ID的数据帧: {ReceivedUnitId}, 期望: {ExpectedUnitId}", unitId, _settings.SlaveId);
                //     return;
                // }

                if (length != frame.Length - 6) // 检查MBAP头中的长度是否与实际后续字节数匹配
                {
                    Log.Warning("播种墙PLC服务: 收到的数据帧长度不匹配。头部长度: {HeaderLength}, 实际数据长度: {ActualLength}", length, frame.Length - 6);
                    return;
                }

                byte[] pdu = frame.Skip(7).ToArray(); // PDU = 功能码 + 数据

                if (_pendingRequests.TryRemove(transactionId, out var tcs))
                {
                    Log.Debug("播种墙PLC服务: 收到事务ID {TransactionId} 的响应, PDU长度 {PduLength}", transactionId, pdu.Length);
                    // 使用PDU完成任务
                    tcs.TrySetResult(pdu);
                }
                else
                {
                    Log.Warning("播种墙PLC服务: 收到未知或延迟的事务ID {TransactionId} 的响应", transactionId);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "播种墙PLC服务: 处理接收到的PLC数据帧时发生错误。");
            }
        }

        // 获取下一个事务ID
        private ushort GetNextTransactionId()
        {
            lock (_transactionIdLock)
            {
                return ++_nextTransactionId;
            }
        }

        // 发送Modbus请求并异步等待响应
        private async Task<byte[]> SendRequestAndWaitForResponseAsync(byte functionCode, byte[] pduData, int timeoutSeconds)
        {
            if (!IsConnected || _tcpClientService == null)
            {
                throw new InvalidOperationException("PLC未连接。");
            }

            ushort transactionId = GetNextTransactionId();
            byte unitId = _settings.SlaveId;
            ushort protocolId = 0; // Modbus TCP协议ID固定为0
            // PDU长度字段 = 单元ID(1字节) + 功能码(1字节) + PDU数据长度
            ushort lengthFieldInMbap = (ushort)(1 + 1 + pduData.Length); 

            byte[] mbapHeader = {
                (byte)(transactionId >> 8), (byte)transactionId,       // 事务ID (MSB, LSB)
                (byte)(protocolId >> 8),    (byte)protocolId,          // 协议ID (MSB, LSB)
                (byte)(lengthFieldInMbap >> 8), (byte)lengthFieldInMbap, // 长度 (MSB, LSB)
                unitId                                                 // 单元ID
            };

            // 完整的请求 = MBAP头 + 功能码 + PDU数据
            byte[] fullRequest = new byte[mbapHeader.Length + 1 + pduData.Length];
            Buffer.BlockCopy(mbapHeader, 0, fullRequest, 0, mbapHeader.Length);
            fullRequest[mbapHeader.Length] = functionCode;
            Buffer.BlockCopy(pduData, 0, fullRequest, mbapHeader.Length + 1, pduData.Length);

            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pendingRequests.TryAdd(transactionId, tcs))
            { 
                // 除非发生ID回绕并发生碰撞，否则不应发生这种情况
                throw new InvalidOperationException($"注册事务ID {transactionId} 的待处理请求失败");
            }

            try
            {
                Log.Debug("播种墙PLC服务: 发送Modbus请求 TID={TransactionId}, FC={FunctionCode}, PDU数据长度={PduDataLength}", 
                          transactionId, functionCode, pduData.Length);
                
                await Task.Run(() => _tcpClientService.Send(fullRequest), _serviceCts.Token); // 通过TcpClientService发送

                // 带超时等待响应
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_serviceCts.Token, timeoutCts.Token);
                
                var responsePdu = await tcs.Task.WaitAsync(linkedCts.Token);
                Log.Debug("播种墙PLC服务: 收到事务ID {TransactionId} 的Modbus响应PDU", transactionId);
                return responsePdu; // 返回的是PDU部分 (功能码 + 数据)
            }
            catch (OperationCanceledException ex) when (_serviceCts.IsCancellationRequested)
            {
                Log.Warning("播种墙PLC服务: 请求TID={TransactionId}因服务关闭而被取消。", transactionId);
                _pendingRequests.TryRemove(transactionId, out _); // 清理待处理的请求
                throw new OperationCanceledException("请求因服务关闭而被取消。", ex, _serviceCts.Token);
            }
            catch (OperationCanceledException ex) // 捕获来自WaitAsync的超时
            {
                Log.Error("播种墙PLC服务: 请求TID={TransactionId}在{Timeout}秒后超时。", transactionId, timeoutSeconds);
                _pendingRequests.TryRemove(transactionId, out _); // 清理待处理的请求
                throw new TimeoutException($"Modbus请求TID={transactionId}超时。", ex);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "播种墙PLC服务: Modbus请求TID={TransactionId}期间发生错误。", transactionId);
                _pendingRequests.TryRemove(transactionId, out _); // 其他错误时清理
                throw;
            }
        }

        /// <summary>
        /// 异步读取保持寄存器。
        /// </summary>
        /// <param name="startAddress">起始地址。</param>
        /// <param name="quantity">要读取的寄存器数量。</param>
        /// <param name="timeoutSeconds">操作超时时间（秒）。</param>
        /// <returns>读取到的寄存器值数组。</returns>
        public async Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort quantity, int timeoutSeconds = 5)
        {
            if (quantity == 0 || quantity > 125) // Modbus协议限制 (125个寄存器 * 2字节/寄存器 = 250字节, 加上其他PDU字节约253-255字节)
            {
                throw new ArgumentOutOfRangeException(nameof(quantity), "数量必须在1到125之间。");
            }

            byte functionCode = 0x03; // 读取保持寄存器功能码
            byte[] pduData = {
                (byte)(startAddress >> 8), (byte)startAddress, // 起始地址 (MSB, LSB)
                (byte)(quantity >> 8),    (byte)quantity     // 数量 (MSB, LSB)
            };

            byte[] responsePdu = await SendRequestAndWaitForResponseAsync(functionCode, pduData, timeoutSeconds);

            // 解析响应PDU
            if (responsePdu.Length < 2) throw new Exception("无效的Modbus响应PDU长度。");

            byte responseFc = responsePdu[0];
            if (responseFc == (functionCode | 0x80)) // Modbus异常响应
            {
                byte exceptionCode = responsePdu[1];
                throw new ModbusException(functionCode, exceptionCode);
            }

            if (responseFc != functionCode) 
            {
                 throw new Exception($"响应中出现意外的Modbus功能码。期望: {functionCode}, 收到: {responseFc}");
            }
            
            // 正常响应: [功能码(1)] [字节计数(1)] [数据(N*2)]
            if (responsePdu.Length < 3) throw new Exception("无效的Modbus读取保持寄存器响应PDU长度。");

            byte byteCount = responsePdu[1];
            if (byteCount != quantity * 2 || responsePdu.Length != 2 + byteCount)
            {
                throw new Exception("Modbus响应字节计数不匹配。");
            }

            var values = new ushort[quantity];
            for (int i = 0; i < quantity; i++)
            {
                // Modbus数据为大端字节序 (MSB first)
                values[i] = (ushort)((responsePdu[2 + i * 2] << 8) | responsePdu[3 + i * 2]);
            }

            return values;
        }
        
        /// <summary>
        /// 异步写入单个寄存器。
        /// </summary>
        /// <param name="registerAddress">寄存器地址。</param>
        /// <param name="value">要写入的值。</param>
        /// <param name="timeoutSeconds">操作超时时间（秒）。</param>
        public async Task WriteSingleRegisterAsync(ushort registerAddress, ushort value, int timeoutSeconds = 5)
        {
            byte functionCode = 0x06; // 写入单个寄存器功能码
            byte[] pduData = {
                (byte)(registerAddress >> 8), (byte)registerAddress, // 寄存器地址 (MSB, LSB)
                (byte)(value >> 8),           (byte)value            // 要写入的值 (MSB, LSB)
            };

            byte[] responsePdu = await SendRequestAndWaitForResponseAsync(functionCode, pduData, timeoutSeconds);

            // 解析响应PDU
            if (responsePdu.Length < 1) throw new Exception("无效的Modbus响应PDU长度。");

            byte responseFc = responsePdu[0];
            if (responseFc == (functionCode | 0x80)) // Modbus异常响应
            {
                byte exceptionCode = responsePdu[1];
                throw new ModbusException(functionCode, exceptionCode);
            }
            
            if (responseFc != functionCode)
            {
                 throw new Exception($"响应中出现意外的Modbus功能码。期望: {functionCode}, 收到: {responseFc}");
            }

            // 正常响应: [功能码(1)] [寄存器地址(2)] [写入的值(2)]
            // 验证回显 (可选但推荐)
            if (responsePdu.Length != 5) throw new Exception("无效的Modbus写入单个寄存器响应PDU长度。");
            ushort echoedAddress = (ushort)((responsePdu[1] << 8) | responsePdu[2]);
            ushort echoedValue = (ushort)((responsePdu[3] << 8) | responsePdu[4]);

            if (echoedAddress != registerAddress || echoedValue != value)
            {
                Log.Warning("Modbus写入单个寄存器响应回显不匹配。地址: {ReqAddr}->{RespAddr}, 值: {ReqVal}->{RespVal}",
                            registerAddress, echoedAddress, value, echoedValue);
                // 根据严格程度，您可以在此处抛出异常。
            }
        }

        // 使所有待处理的请求失败
        private void FailAllPendingRequests(Exception exception)
        {
            Log.Warning("由于以下原因，所有 ({Count}) 待处理的Modbus请求均失败: {ExceptionType} - {ExceptionMessage}", 
                        _pendingRequests.Count, exception.GetType().Name, exception.Message);
            foreach (var kvp in _pendingRequests)
            {
                if (_pendingRequests.TryRemove(kvp.Key, out var tcs))
                {
                    tcs.TrySetException(exception);
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                Log.Debug("播种墙PLC服务: 正在释放...");
                _serviceCts.Cancel(); // 向正在进行的操作发送取消信号
                DisconnectAsync().Wait(1000); // 尝试优雅断开连接 (带超时)
                FailAllPendingRequests(new ObjectDisposedException(nameof(SowingWallPlcService)));
                _connectSemaphore.Dispose();
                _serviceCts.Dispose();
                Log.Debug("播种墙PLC服务: 已释放。");
            }
        }
    }

    /// <summary>
    /// Modbus错误自定义异常类
    /// </summary>
    public class ModbusException : Exception
    {
        public byte FunctionCode { get; }
        public byte ExceptionCode { get; }

        public ModbusException(byte functionCode, byte exceptionCode)
            : base(GetMessage(exceptionCode))
        {
            FunctionCode = functionCode;
            ExceptionCode = exceptionCode;
            Log.Error("发生Modbus异常: FC={FunctionCode}, ExceptionCode={ExceptionCode} ({Message})", 
                      FunctionCode, ExceptionCode, Message);
        }

        private static string GetMessage(byte exceptionCode)
        {
            return exceptionCode switch
            {
                0x01 => "非法功能",
                0x02 => "非法数据地址",
                0x03 => "非法数据值",
                0x04 => "从站设备故障",
                0x05 => "确认", // Acknowledge (通常用于长操作的中间响应)
                0x06 => "从站设备忙",
                0x08 => "存储奇偶校验错误",
                0x0A => "网关路径不可用",
                0x0B => "网关目标设备响应失败",
                _ => $"未知的Modbus异常代码: {exceptionCode:X2}"
            };
        }
    }
} 