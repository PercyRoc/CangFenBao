using System.Collections.Concurrent;
using Common.Services.Settings;
using Serilog;
using XinBeiYang.Models;
using XinBeiYang.Models.Communication.JdWcs;
using System.IO;
using System.Text.Json;
using System.Text;
using DeviceService.DataSourceDevices.TCP; // 添加 TcpClientService 命名空间

namespace XinBeiYang.Services;

/// <summary>
/// 京东WCS通信服务接口
/// </summary>
public interface IJdWcsCommunicationService : IDisposable
{
    /// <summary>
    /// 连接状态变更事件
    /// </summary>
    event EventHandler<bool> ConnectionChanged;
    
    /// <summary>
    /// 是否已连接
    /// </summary>
    bool IsConnected { get; }
    
    /// <summary>
    /// 启动服务
    /// </summary>
    void Start();
    
    /// <summary>
    /// 停止服务
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// 上传图片地址
    /// </summary>
    /// <param name="taskNo">包裹流水号</param>
    /// <param name="barcode">一维码列表</param>
    /// <param name="matrixBarcode">二维码列表</param>
    /// <param name="absoluteImageUrls">图片绝对路径列表</param>
    /// <param name="timestamp">时间戳</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>上传结果</returns>
    Task<bool> UploadImageUrlsAsync(int taskNo, List<string> barcode, List<string> matrixBarcode, List<string> absoluteImageUrls, long timestamp, CancellationToken cancellationToken = default);
}

/// <summary>
/// 京东WCS通信服务实现
/// </summary>
/// <remarks>
/// 构造函数
/// </remarks>
/// <param name="settingsService">设置服务</param>
public class JdWcsCommunicationService(ISettingsService settingsService) : IJdWcsCommunicationService
{
    private readonly ISettingsService _settingsService = settingsService;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<AckMessage>> _pendingCommands = new();
    private HostConfiguration _config = settingsService.LoadSettings<HostConfiguration>();
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    
    // 替换 TcpClient 和 NetworkStream 为 TcpClientService
    private TcpClientService? _tcpClientService;
    private readonly List<byte> _receivedBuffer = new(); // 用于接收数据的缓冲区
    private readonly object _receivedBufferLock = new(); // 保护缓冲区的锁
    
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _heartbeatTask;
    private bool _disposed;
    private bool _isConnected;
    private int _messageSequence;
    private readonly object _sendLock = new();

    /// <inheritdoc />
    public event EventHandler<bool>? ConnectionChanged;
    
    /// <inheritdoc />
    public bool IsConnected => _isConnected;

    /// <inheritdoc />
    public void Start()
    {
        _config = _settingsService.LoadSettings<HostConfiguration>();
        
        if (_cancellationTokenSource != null) return;

        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;

        // 启动连接任务
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!IsConnected)
                    {
                        await ConnectAsync();
                    }

                    await Task.Delay(JdWcsConstants.ReconnectInterval, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "京东WCS连接任务异常");
                    if (_isConnected) // 只有当状态从 true 变为 false 时才更新和触发
                    {
                        _isConnected = false;
                        ConnectionChanged?.Invoke(this, false);
                        Log.Information("京东WCS连接状态（异常捕获）: 已断开");
                    }
                }
            }
        }, token);
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        if (_cancellationTokenSource == null) return;
        
        try
        {
            await _cancellationTokenSource.CancelAsync();
            await DisconnectAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止京东WCS通信服务时发生错误");
        }
        finally
        {
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
        }
    }
    
    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
    
    /// <summary>
    /// 释放资源
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            Log.Information("正在释放京东WCS通信服务资源...");
            
            // 取消所有任务
            _cancellationTokenSource?.Cancel();
            
            // 同步清理待处理请求
            foreach (var command in _pendingCommands.Values)
            {
                command.TrySetCanceled();
            }
            _pendingCommands.Clear();
            
            // 立即释放TcpClientService
            _tcpClientService?.Dispose();
            _tcpClientService = null;
            
            // 释放取消令牌
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            
            Log.Information("京东WCS通信服务资源已释放");
        }
        _disposed = true;
    }
    
    /// <summary>
    /// 连接到京东WCS服务器
    /// </summary>
    private async Task ConnectAsync()
    {
        try
        {
            _config = _settingsService.LoadSettings<HostConfiguration>();
            var ipAddress = _config.JdIpAddress;
            var port = _config.JdPort;
            
            Log.Information("连接京东WCS服务器 {IpAddress}:{Port}", ipAddress, port);
            
            // 释放现有连接（如果有）
            if (_tcpClientService != null)
            {
                _tcpClientService.Dispose();
                _tcpClientService = null;
            }
            
            // 创建TCP客户端并连接
            _tcpClientService = new TcpClientService(
                "JdWCS", // 设备名称
                ipAddress,
                port,
                OnDataReceived, // 数据接收回调
                OnConnectionStatusChanged // 启用自动重连
            );
            
            // TcpClientService.Connect是同步方法，设置超时
            await Task.Run(() => {
                try {
                    _tcpClientService.Connect(JdWcsConstants.ConnectionTimeout);
                }
                catch (Exception ex) {
                    Log.Error(ex, "连接京东WCS服务器失败");
                    throw;
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "连接京东WCS服务器时发生错误");
            await DisconnectAsync();
        }
    }
    
    // 处理连接状态变更的回调
    private void OnConnectionStatusChanged(bool isConnected)
    {
        Log.Debug("[JdWCS] OnConnectionStatusChanged received callback with isConnected = {IsConnected}. Current internal _isConnected = {_CurrentIsConnected}", isConnected, _isConnected);
        if (_disposed) 
        {
            Log.Debug("[JdWCS] Service is disposed, ignoring OnConnectionStatusChanged callback.");
            return;
        }
        
        // --- 修改开始：与PLC逻辑对齐 ---
        // 1. 始终触发事件
        var handler = ConnectionChanged;
        if (handler != null)
        {
            try
            {
                handler.Invoke(this, isConnected);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[JdWCS] Error invoking ConnectionChanged event handler.");
            }
        }
        else
        {
            Log.Warning("[JdWCS] ConnectionChanged event has no subscribers.");
        }

        // 2. 更新内部状态
        bool previousState = _isConnected;
        _isConnected = isConnected; 
        
        // 3. 根据新状态执行后续操作
        Log.Information("京东WCS连接状态: {Status}", _isConnected ? "已连接" : "已断开"); // 记录状态变化
        
        if (isConnected)
        {
            // 启动心跳任务
            if (_heartbeatTask == null || _heartbeatTask.IsCompleted)
            {
                // 确保 CancellationTokenSource 存在且未被取消
                if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
                {
                    _heartbeatTask = StartHeartbeatTaskAsync(_cancellationTokenSource!.Token);
                    Log.Debug("京东WCS心跳任务已启动");
                }
                else
                {
                    Log.Warning("无法启动心跳任务，因为 CancellationTokenSource 为 null 或已取消。");
                }
            }
            else
            {
                Log.Debug("心跳任务已在运行，无需重新启动。");
            }
        }
        else
        {
            // 清理所有等待的请求
            foreach (var command in _pendingCommands.Values)
            {
                command.TrySetCanceled();
            }
            _pendingCommands.Clear();
            Log.Debug("京东WCS连接断开，已清理待处理的命令。");
        }
        // --- 修改结束 ---
    }
    
    // 处理接收到数据的回调
    private void OnDataReceived(byte[] data)
    {
        if (_disposed) return;
        
        lock (_receivedBufferLock)
        {
            _receivedBuffer.AddRange(data);
            ProcessReceivedBuffer(_cancellationTokenSource!.Token);
        }
    }
    
    // 处理接收缓冲区中的数据
    private void ProcessReceivedBuffer(CancellationToken cancellationToken)
    {
        try
        {
            while (_receivedBuffer.Count >= JdWcsMessageHeader.Size)
            {
                // 尝试解析消息头
                var headerData = _receivedBuffer.Take(JdWcsMessageHeader.Size).ToArray();
                var header = JdWcsMessageHeader.FromBytes(headerData);
                
                // 验证魔数
                if (header.MagicNumber != JdWcsConstants.MagicNumber)
                {
                    Log.Warning("接收到的消息魔数不正确: {MagicNumber}", header.MagicNumber);
                    _receivedBuffer.RemoveAt(0); // 移除第一个字节并继续
                    continue;
                }
                
                // 检查缓冲区是否包含完整消息（头部+数据部分）
                int totalLength = JdWcsMessageHeader.Size + header.DataLength;
                if (_receivedBuffer.Count < totalLength)
                {
                    // 消息不完整，等待更多数据
                    break;
                }
                
                // 提取消息体
                byte[] bodyBuffer = new byte[0];
                if (header.DataLength > 0)
                {
                    bodyBuffer = _receivedBuffer.Skip(JdWcsMessageHeader.Size).Take(header.DataLength).ToArray();
                }
                
                // 从缓冲区移除已处理的数据
                _receivedBuffer.RemoveRange(0, totalLength);
                
                // 在单独的任务中处理消息，避免阻塞接收线程
                _ = Task.Run(() => ProcessMessageAsync(header, bodyBuffer, cancellationToken), cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理接收缓冲区时发生错误");
        }
    }
    
    /// <summary>
    /// 根据请求消息类型获取对应的ACK消息类型
    /// </summary>
    private JdWcsMessageType GetAckTypeForRequest(JdWcsMessageType requestType)
    {
        // 基础ACK类型 = 请求类型 + 1000 (基于协议约定)
        var baseAckType = (short)((short)requestType + 1000);
        
        // 检查计算出的ACK类型是否在枚举中定义
        if (Enum.IsDefined(typeof(JdWcsMessageType), baseAckType))
        {
            return (JdWcsMessageType)baseAckType;
        }
        
        // 如果没有找到精确匹配，返回HeartbeatAck作为备用
        Log.Warning("无法找到请求类型 {RequestType} 对应的特定ACK类型，将使用 HeartbeatAck", requestType);
        return JdWcsMessageType.HeartbeatAck; 
    }
    
    /// <summary>
    /// 处理接收到的消息
    /// </summary>
    private async Task ProcessMessageAsync(JdWcsMessageHeader header, byte[] bodyBuffer, CancellationToken cancellationToken)
    {
        bool ackSent = false; // 标记是否已发送ACK
        JdWcsMessageType ackType = JdWcsMessageType.HeartbeatAck; // 默认ACK类型
        bool needsSpecificAck = header.NeedAck == 1 && (short)header.MessageType < 1000;
        
        if (needsSpecificAck)
        {
             ackType = GetAckTypeForRequest(header.MessageType);
        }
        
        try
        {
            // 检查数据格式（我们期望JSON）
            if (header.DataFormat != 1)
            {
                Log.Warning("接收到非JSON格式的消息体, 格式: {Format}, 类型: {Type}", header.DataFormat, header.MessageType);
                if (needsSpecificAck)
                {
                    await SendAckAsync(ackType, header.MessageSequence, false, cancellationToken);
                    ackSent = true;
                }
                return;
            }
            
            switch (header.MessageType)
            {
                // 处理来自WCS的ACK消息 (这些消息本身不需要回复ACK)
                case JdWcsMessageType.HeartbeatAck:
                case JdWcsMessageType.ScanReportAck:
                case JdWcsMessageType.ImageUrlReportAck:
                case JdWcsMessageType.SortingCellAssignAck:
                case JdWcsMessageType.SortingResultReportAck:
                case JdWcsMessageType.CellStatusReportAck:
                case JdWcsMessageType.CellRfidBindingReportAck:
                case JdWcsMessageType.SpeedQueryAck:
                case JdWcsMessageType.SpeedAdjustmentAck:
                case JdWcsMessageType.CellStatusControlAck:
                case JdWcsMessageType.DeviceModeReportAck:
                // case JdWcsMessageType.Ack: // 通用ACK，如果需要处理的话
                    ProcessAckMessage(header, bodyBuffer);
                    break; // ACK消息不需要回复ACK
                
                // --- 处理来自WCS的请求消息 --- 
                // 示例: 处理分拣格口下发
                case JdWcsMessageType.SortingCellAssign:
                    ProcessSortingCellAssign(bodyBuffer); // 实现这个方法来处理业务逻辑
                    if (needsSpecificAck)
                    {
                        await SendAckAsync(ackType, header.MessageSequence, true, cancellationToken);
                        ackSent = true;
                    }
                    break;
                    
                // 示例: 处理格口状态控制
                case JdWcsMessageType.CellStatusControl:
                    ProcessCellStatusControl(bodyBuffer); // 实现这个方法
                     if (needsSpecificAck)
                    {
                        await SendAckAsync(ackType, header.MessageSequence, true, cancellationToken);
                        ackSent = true;
                    }
                    break;
                    
                // TODO: 添加处理其他需要ACK的请求消息的case...
                // case JdWcsMessageType.SpeedQuery:
                // case JdWcsMessageType.SpeedAdjustment:
                
                default:
                    Log.Warning("接收到未处理类型的消息: {MessageType}", header.MessageType);
                    // 如果未知消息也需要ACK (根据协议可能需要)
                    if (needsSpecificAck)
                    {
                         Log.Warning("为未处理的消息类型 {MessageType} 发送失败ACK", header.MessageType);
                         await SendAckAsync(ackType, header.MessageSequence, false, cancellationToken);
                         ackSent = true;
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理消息时发生错误, 类型: {Type}, 序号: {Sequence}", header.MessageType, header.MessageSequence);
            // 发生异常时发送失败ACK (如果需要且尚未发送)
            if (needsSpecificAck && !ackSent)
            {
                 try { await SendAckAsync(ackType, header.MessageSequence, false, cancellationToken); } catch { /* Ignore ACK send error */ }
            }
        }
    }
    
    // --- Placeholder methods for processing WCS requests --- 
    private void ProcessSortingCellAssign(byte[] bodyBuffer)
    {
        try
        {
            // TODO: 实现解析SortingCellAssign消息体并处理业务逻辑
            var json = Encoding.UTF8.GetString(bodyBuffer);
            Log.Information("收到分拣格口下发: {Json}", json);
            // var message = JsonSerializer.Deserialize<SortingCellAssignMessage>(json, _jsonOptions);
            // ... 处理 message ...
        }
        catch (Exception ex)
        { 
            Log.Error(ex, "处理分拣格口下发消息时出错");
            throw; // Rethrow to allow sending failure ACK
        }
    }
    
    private void ProcessCellStatusControl(byte[] bodyBuffer)
    {
        try
        {
            // TODO: 实现解析CellStatusControl消息体并处理业务逻辑
            var json = Encoding.UTF8.GetString(bodyBuffer);
            Log.Information("收到格口状态控制: {Json}", json);
            // var message = JsonSerializer.Deserialize<CellStatusControlMessage>(json, _jsonOptions);
            // ... 处理 message ...
        }
        catch (Exception ex)
        { 
            Log.Error(ex, "处理格口状态控制消息时出错");
            throw; // Rethrow to allow sending failure ACK
        }
    }
    
    /// <summary>
    /// 处理ACK消息
    /// </summary>
    private void ProcessAckMessage(JdWcsMessageHeader header, byte[] bodyBuffer)
    { 
        try
        { 
            if (bodyBuffer.Length == 0)
            {
                 Log.Warning("收到空的ACK消息体, 类型: {Type}, 序号: {Sequence}", header.MessageType, header.MessageSequence);
                 if (_pendingCommands.TryRemove(header.MessageSequence, out var emptyTcs))
                 { 
                     emptyTcs.TrySetResult(new AckMessage { Code = JdWcsConstants.AckFailure, DeviceNo = "UNKNOWN" });
                 }
                 return;
            }

            var json = Encoding.UTF8.GetString(bodyBuffer);
            var ackMessage = JsonSerializer.Deserialize<AckMessage>(json, _jsonOptions);

            if (ackMessage == null)
            { 
                Log.Error("无法反序列化ACK消息JSON: {Json}", json);
                if (_pendingCommands.TryRemove(header.MessageSequence, out var deserializeTcs))
                { 
                     deserializeTcs.TrySetException(new JsonException("Failed to deserialize ACK message"));
                }
                return;
            }

            if (_pendingCommands.TryRemove(header.MessageSequence, out var tcs))
            {
                tcs.TrySetResult(ackMessage);
            }
            else
            {
                Log.Warning("收到未知的ACK消息, 序号: {Sequence}", header.MessageSequence);
            }
        }
        catch (JsonException jsonEx)
        {
             Log.Error(jsonEx, "解析ACK消息JSON时发生错误, 序号: {Sequence}", header.MessageSequence);
             if (_pendingCommands.TryRemove(header.MessageSequence, out var jsonTcs))
             { 
                 jsonTcs.TrySetException(jsonEx);
             }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理ACK消息时发生内部错误, 序号: {Sequence}", header.MessageSequence);
            if (_pendingCommands.TryRemove(header.MessageSequence, out var errorTcs))
            { 
                 errorTcs.TrySetException(ex);
            }
        }
    }
    
    /// <summary>
    /// 发送ACK确认消息
    /// </summary>
    /// <param name="ackType">要发送的ACK消息类型</param>
    /// <param name="messageSequence">要确认的原始消息序号</param>
    /// <param name="isSuccess">确认结果</param>
    /// <param name="cancellationToken">取消令牌</param>
    private async Task SendAckAsync(JdWcsMessageType ackType, int messageSequence, bool isSuccess, CancellationToken cancellationToken)
    {
        try
        {
            // 使用配置中的主设备号
            var ack = new AckMessage
            {
                DeviceNo = _config.DeviceId, 
                Code = isSuccess ? JdWcsConstants.AckSuccess : JdWcsConstants.AckFailure
            };

            var json = JsonSerializer.Serialize(ack, _jsonOptions);
            var bodyBytes = Encoding.UTF8.GetBytes(json);
            
            // 使用传入的ackType发送ACK消息
            await SendMessageAsync(ackType, messageSequence, bodyBytes, false, cancellationToken); 
        }
        catch (Exception ex)
        { 
            Log.Error(ex, "发送ACK确认消息时发生错误, 类型: {AckType}, 序号: {Sequence}", ackType, messageSequence);
        }
    }
    
    /// <summary>
    /// 启动心跳任务
    /// </summary>
    private Task StartHeartbeatTaskAsync(CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && IsConnected)
                {
                    try
                    {
                        await SendHeartbeatAsync(cancellationToken);
                        await Task.Delay(JdWcsConstants.HeartbeatInterval, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "发送心跳包时发生错误");
                        await DisconnectAsync();
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "心跳任务异常");
            }
        }, cancellationToken);
    }
    
    /// <summary>
    /// 发送心跳包
    /// </summary>
    private async Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            var heartbeat = new HeartbeatMessage
            {
                DeviceNo = _config.DeviceId, 
                DeviceStatus = JdDeviceStatus.Running
            };

            var json = JsonSerializer.Serialize(new 
            {
                 deviceNo = heartbeat.DeviceNo,
                 deviceStatus = (int)heartbeat.DeviceStatus 
            }, _jsonOptions);
            var bodyBytes = Encoding.UTF8.GetBytes(json);

            var sequence = Interlocked.Increment(ref _messageSequence);
            var ack = await SendMessageAsync(JdWcsMessageType.Heartbeat, sequence, bodyBytes, true, cancellationToken);

            if (ack == null || ack.Code != JdWcsConstants.AckSuccess)
            {
                Log.Warning("心跳消息未得到成功确认, 序号: {Sequence}", sequence);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "发送心跳包时发生错误");
            await DisconnectAsync();
        }
    }
    
    /// <summary>
    /// 断开与京东WCS服务器的连接
    /// </summary>
    private async Task DisconnectAsync()
    {
        try
        {
            // 清除所有待处理的命令
            foreach (var command in _pendingCommands.Values)
            {
                command.TrySetCanceled();
            }
            _pendingCommands.Clear();
            
            // 关闭并释放TCP客户端
            _tcpClientService?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "断开京东WCS服务器连接时发生错误");
        }
        finally
        {
            _tcpClientService = null;
            _isConnected = false;
            
            // 等待任务完成
            if (_heartbeatTask != null)
            {
                try
                {
                    await _heartbeatTask;
                }
                catch (Exception)
                {
                    // 忽略任务取消异常
                }
                _heartbeatTask = null;
            }
        }
    }
    
    /// <summary>
    /// 发送消息 (核心发送逻辑)
    /// </summary>
    private async Task<AckMessage?> SendMessageAsync(JdWcsMessageType messageType, int sequence, byte[] bodyBytes, bool needAck, CancellationToken cancellationToken)
    {
        if (!IsConnected || _tcpClientService == null)
        {
            Log.Warning("尝试发送消息但未连接, 类型: {Type}, 序号: {Sequence}", messageType, sequence);
            return null;
        }
        
        TaskCompletionSource<AckMessage>? tcs = null;
        var commandKey = sequence;
        
        if (needAck)
        {
            tcs = new TaskCompletionSource<AckMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingCommands.AddOrUpdate(commandKey, tcs, (_, _) => tcs);
        }
        
        try
        {
            var header = new JdWcsMessageHeader
            {
                MagicNumber = JdWcsConstants.MagicNumber,
                MessageType = messageType,
                MessageSequence = sequence,
                DataLength = bodyBytes.Length,
                ProtocolVersion = JdWcsConstants.ProtocolVersion,
                DataFormat = 1,
                VendorId = (short)_config.VendorId,
                DeviceType = (sbyte)_config.DeviceType,
                NeedAck = needAck ? (sbyte)1 : (sbyte)0,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            
            var headerBytes = header.ToBytes();
            var fullMessage = new byte[headerBytes.Length + bodyBytes.Length];
            Buffer.BlockCopy(headerBytes, 0, fullMessage, 0, headerBytes.Length);
            Buffer.BlockCopy(bodyBytes, 0, fullMessage, headerBytes.Length, bodyBytes.Length);

            AckMessage? ackResult = null;
            bool messageSentSuccessfully = false;

            for (int retryCount = 0; retryCount <= JdWcsConstants.MaxRetryCount && !cancellationToken.IsCancellationRequested; retryCount++)
            {
                if (!IsConnected || _tcpClientService == null)
                {
                    Log.Warning("连接断开，无法发送消息 (重试 {RetryCount}), 类型: {Type}, 序号: {Sequence}", retryCount, messageType, sequence);
                    _pendingCommands.TryRemove(commandKey, out _);
                    return null; 
                }
                
                try
                {
                    lock (_sendLock)
                    {
                        // 使用TcpClientService发送数据
                        _tcpClientService.Send(fullMessage);
                    }
                    messageSentSuccessfully = true;
                    Log.Debug("消息已发送 (尝试 {RetryCount}), 类型: {Type}, 序号: {Sequence}, 长度: {Length}", retryCount + 1, messageType, sequence, fullMessage.Length);

                    if (needAck && tcs != null)
                    {
                        using var timeoutCts = new CancellationTokenSource(JdWcsConstants.MessageResponseTimeout);
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                        
                        try
                        {
                            ackResult = await tcs.Task.WaitAsync(linkedCts.Token);
                            if (ackResult.Code == JdWcsConstants.AckSuccess)
                            {
                                Log.Debug("收到成功ACK, 类型: {Type}, 序号: {Sequence}", messageType, sequence);
                                _pendingCommands.TryRemove(commandKey, out _);
                                return ackResult;
                            }
                            else
                            {
                                Log.Warning("收到失败ACK (Code: {AckCode}), 类型: {Type}, 序号: {Sequence}, 重试 {RetryCount}/{MaxRetry}", 
                                    ackResult.Code, messageType, sequence, retryCount + 1, JdWcsConstants.MaxRetryCount);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            if (timeoutCts.IsCancellationRequested)
                            {
                                Log.Warning("等待ACK响应超时, 类型: {Type}, 序号: {Sequence}, 重试 {RetryCount}/{MaxRetry}", 
                                    messageType, sequence, retryCount + 1, JdWcsConstants.MaxRetryCount);
                            }
                            else
                            {
                                Log.Information("发送操作被取消, 类型: {Type}, 序号: {Sequence}", messageType, sequence);
                                _pendingCommands.TryRemove(commandKey, out _);
                                throw;
                            }
                        }
                        catch (Exception ackEx)
                        {
                             Log.Error(ackEx, "等待或处理ACK时出错, 类型: {Type}, 序号: {Sequence}, 重试 {RetryCount}/{MaxRetry}", 
                                 messageType, sequence, retryCount + 1, JdWcsConstants.MaxRetryCount);
                        }
                    }
                    else
                    {
                        return null;
                    }
                }
                catch (IOException ioEx)
                {
                    Log.Error(ioEx, "发送消息IO错误 (重试 {RetryCount}), 类型: {Type}, 序号: {Sequence}", retryCount, messageType, sequence);
                    await DisconnectAsync();
                    _pendingCommands.TryRemove(commandKey, out _);
                    return null;
                }
                catch (Exception sendEx)
                {
                    Log.Error(sendEx, "发送消息时发生内部错误 (重试 {RetryCount}), 类型: {Type}, 序号: {Sequence}", retryCount, messageType, sequence);
                    messageSentSuccessfully = false;
                }
                
                if (retryCount < JdWcsConstants.MaxRetryCount && !cancellationToken.IsCancellationRequested)
                { 
                     await Task.Delay(100, cancellationToken);
                }
            }
            
            if (needAck) 
            { 
                 Log.Error("消息发送失败或未收到有效ACK，已达到最大重试次数, 类型: {Type}, 序号: {Sequence}", messageType, sequence);
                 _pendingCommands.TryRemove(commandKey, out _);
            }
            else if (!messageSentSuccessfully)
            {
                 Log.Error("消息发送失败 (无需ACK), 类型: {Type}, 序号: {Sequence}", messageType, sequence);
            }

            return ackResult;
        }
        catch (Exception ex)
        { 
            Log.Error(ex, "SendMessageAsync 内部发生严重错误, 类型: {Type}, 序号: {Sequence}", messageType, sequence);
            if (needAck)
            { 
                _pendingCommands.TryRemove(commandKey, out _);
            }
            await DisconnectAsync();
            throw;
        }
    }

    /// <summary>
    /// 上传图片地址
    /// </summary>
    /// <param name="taskNo">包裹流水号</param>
    /// <param name="barcode">一维码列表</param>
    /// <param name="matrixBarcode">二维码列表</param>
    /// <param name="absoluteImageUrls">图片绝对路径列表</param>
    /// <param name="timestamp">时间戳</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>上传结果</returns>
    public async Task<bool> UploadImageUrlsAsync(int taskNo, List<string> barcode, List<string> matrixBarcode, List<string> absoluteImageUrls, long timestamp, CancellationToken cancellationToken = default)
    {
        if (absoluteImageUrls.Count == 0)
        {
            Log.Warning("上传图片地址列表为空, TaskNo: {TaskNo}", taskNo);
            return false;
        }

        try
        { 
            var deviceNo = _config.DeviceId; 
            var urlPrefix = _config.JdLocalHttpUrlPrefix.TrimEnd('/');
            var imageBaseStoragePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Images"); 

            var finalUrls = new List<string>();
            foreach (var absolutePath in absoluteImageUrls)
            { 
                if (string.IsNullOrEmpty(absolutePath)) continue;
                try
                { 
                    var relativePath = Path.GetRelativePath(imageBaseStoragePath, absolutePath);
                    var urlRelativePath = relativePath.Replace(Path.DirectorySeparatorChar, '/');
                    finalUrls.Add(urlPrefix + "/" + urlRelativePath);
                }
                catch (Exception ex)
                { 
                     Log.Error(ex, "处理图片路径时出错: Base={BasePath}, Absolute={AbsolutePath}, TaskNo={TaskNo}", 
                         imageBaseStoragePath, absolutePath, taskNo);
                }
            }

            if (!finalUrls.Any())
            { 
                Log.Error("无法为 TaskNo: {TaskNo} 构造任何有效的图片URL", taskNo);
                return false;
            }

            const int maxMessages = 1;
            var totalMessages = maxMessages; 

            var success = true;
            for (var i = 0; i < totalMessages; i++)
            {
                var imageUrlString = string.Join(",", finalUrls);

                var message = new ImageUrlReportMessage
                {
                    DeviceNo = deviceNo,
                    TaskNo = taskNo,
                    Barcode = barcode.Count == 0 || barcode[0] == "noread" ? new List<string> { "noread" } : barcode,
                    MatrixBarcode = matrixBarcode,
                    ImageUrl = imageUrlString, 
                    Timestamp = timestamp,
                    PicQty = (sbyte)absoluteImageUrls.Count,
                    MsgQty = (sbyte)totalMessages,
                    MsgSeq = (sbyte)(i + 1)
                };
                
                var json = JsonSerializer.Serialize(message, _jsonOptions);
                var bodyBytes = Encoding.UTF8.GetBytes(json);

                var sequence = Interlocked.Increment(ref _messageSequence);
                var ack = await SendMessageAsync(JdWcsMessageType.ImageUrlReport, sequence, bodyBytes, true, cancellationToken);

                if (ack == null || ack.Code != JdWcsConstants.AckSuccess)
                {
                    Log.Warning("图片地址上报消息未得到成功确认, TaskNo: {TaskNo}, 序号: {Sequence}, 批次: {Batch}/{TotalBatch}", 
                        taskNo, sequence, i + 1, totalMessages);
                    success = false;
                }
                else
                {
                     Log.Information("图片地址上报成功, TaskNo: {TaskNo}, 序号: {Sequence}, 批次: {Batch}/{TotalBatch}, 图片数: {Count}", 
                         taskNo, sequence, i + 1, totalMessages, finalUrls.Count);
                }
            }

            return success;
        }
        catch (Exception ex)
        { 
            Log.Error(ex, "上传图片地址时发生严重错误, TaskNo: {TaskNo}", taskNo);
            return false;
        }
    }
} 