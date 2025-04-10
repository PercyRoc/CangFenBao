using System.Collections.Concurrent;
using System.Net.Sockets;
using Common.Services.Settings;
using Serilog;
using XinBeiYang.Models;
using XinBeiYang.Models.Communication;
using XinBeiYang.Models.Communication.Packets;

namespace XinBeiYang.Services;

/// <summary>
///     PLC通讯服务实现
/// </summary>
internal class PlcCommunicationService(
    ISettingsService settingsService) : IPlcCommunicationService, IDisposable
{
    private CancellationTokenSource _cancellationTokenSource = new();
    private readonly object _connectionLock = new();
    private readonly ConcurrentDictionary<ushort, TaskCompletionSource<PlcPacket>> _pendingRequests = new();
    private Task? _heartbeatTask;
    private bool _isDisposed;
    private DateTime _lastReceivedTime = DateTime.MinValue;
    private NetworkStream? _networkStream;
    private ushort _nextCommandId = 1;
    private Task? _receiveTask;
    private TcpClient? _tcpClient;
    private string? _lastIpAddress;
    private int _lastPort;
    private Task? _reconnectTask;
    private bool _isStopping;
    private DeviceStatusCode CurrentDeviceStatus { get; set; } = DeviceStatusCode.Normal;

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isStopping = true;
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
        _networkStream?.Dispose();
        _tcpClient?.Dispose();

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    public event EventHandler<bool>? ConnectionStatusChanged;

    public bool IsConnected => _tcpClient?.Connected ?? false;

    public async Task ConnectAsync(string ipAddress, int port)
    {
        lock (_connectionLock)
        {
            if (_tcpClient?.Connected == true)
                return;
        }

        try
        {
            _lastIpAddress = ipAddress;
            _lastPort = port;

            _tcpClient = new TcpClient();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            
            try 
            {
                await _tcpClient.ConnectAsync(ipAddress, port, timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("连接PLC服务器超时");
            }

            _networkStream = _tcpClient.GetStream();

            // 启动接收任务 - 使用主取消令牌
            _receiveTask = Task.Run(ReceiveLoopAsync, _cancellationTokenSource.Token);

            // 启动心跳任务 - 使用主取消令牌
            _heartbeatTask = Task.Run(HeartbeatLoopAsync, _cancellationTokenSource.Token);

            _lastReceivedTime = DateTime.Now; // 更新最后接收时间
            ConnectionStatusChanged?.Invoke(this, true);
            // 同时触发设备状态变更事件
            DeviceStatusChanged?.Invoke(this, DeviceStatusCode.Normal);
            Log.Information("已连接到PLC服务器 {IpAddress}:{Port}", ipAddress, port);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "连接PLC服务器失败");
            ConnectionStatusChanged?.Invoke(this, false); // 确保连接失败时也触发事件
            // 同时触发设备状态变更事件
            DeviceStatusChanged?.Invoke(this, DeviceStatusCode.Disconnected);
            await DisconnectAsync();
            throw; // 重新抛出异常以便重连循环处理
        }
    }

    public async Task DisconnectAsync()
    {
        try
        {
            Log.Information("开始断开PLC连接流程...");

            // 1. 取消所有关联任务 (使用同步 Cancel)
            _cancellationTokenSource.Cancel();

            // 2. 等待关键后台任务完成 (增加超时)
            var tasksToAwait = new List<Task?>
            {
                _receiveTask,
                _heartbeatTask,
                _reconnectTask // 添加重连任务到等待列表
            }.Where(t => t != null).ToList(); // Filter out nulls early

            if (tasksToAwait.Count > 0)
            {
                Log.Debug("开始等待后台任务完成 (超时 {TimeoutSeconds} 秒)...", 2);
                try
                {
                    // 等待所有任务完成，或者等待 2 秒超时
                    var allTasks = Task.WhenAll(tasksToAwait!);
                    var completedTask = await Task.WhenAny(allTasks, Task.Delay(TimeSpan.FromSeconds(2)));

                    if (completedTask == allTasks)
                    {
                        Log.Debug("所有后台任务在超时前正常完成或被取消");
                        // 可以选择性地等待任务结果/异常，但 Cancel 后预期是 OCE
                        await allTasks; 
                    }
                    else
                    {                    
                        Log.Warning("等待后台任务超时，将强制关闭网络资源");
                    }
                }
                catch (OperationCanceledException)
                {
                    Log.Debug("等待后台任务时捕获到 OperationCanceledException (预期行为)");
                }
                catch (Exception ex)
                {
                     Log.Warning(ex, "等待后台任务时发生意外异常");
                }
            }

            // 3. 强制关闭并释放网络资源 (无论任务是否超时)
            Log.Debug("开始关闭并释放网络资源...");
            try
            {
                // 即使流/客户端已经是 null 或已关闭/释放，再次调用通常是安全的
                _networkStream?.Close(); 
                _tcpClient?.Close();
                _networkStream?.Dispose();
                _tcpClient?.Dispose();
                Log.Debug("网络资源已关闭并释放");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "关闭或释放网络资源时发生次要错误");
            }
            finally
            {
                _networkStream = null;
                _tcpClient = null;
            }
            
            // 4. 在任务结束后取消并清除待处理请求
            Log.Debug("正在取消并清除所有剩余的待处理PLC请求...");
            foreach (var kvp in _pendingRequests)
            {
                kvp.Value.TrySetCanceled(); // 不需要 token，直接取消
            }
            _pendingRequests.Clear(); // 清空字典
            Log.Debug("已清除所有待处理的PLC请求。");

            // 重置任务引用
            _receiveTask = null;
            _heartbeatTask = null;

            // 5. 更新状态和触发事件 (保持不变)
            Log.Information("已断开与PLC服务器的连接，并清理了相关任务");

            // 6. 创建新的取消令牌源以备下次连接
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();

            // 7. 启动重连任务 (如果需要)
            if (_lastIpAddress != null && !_isStopping)
            {
                // 确保之前的重连任务（如果存在）已处理
                // 注意：这里的 _reconnectTask 应该是 null 或已被取消
                 if (_reconnectTask?.IsCompleted == false)
                 {
                      Log.Warning("之前的重连任务可能未完全结束，等待短暂时间...");
                      // 可以选择短暂等待或忽略，取决于具体逻辑
                      // await Task.Delay(100); // 可选的短暂等待
                 }
                _reconnectTask = Task.Run(ReconnectLoopAsync, _cancellationTokenSource.Token); // 使用新的CTS
                Log.Information("已启动新的PLC重连任务");
            }
            else if (_isStopping)
            {
                Log.Information("服务正在停止，不启动新的重连任务");
            }
            else
            {
                Log.Warning("无法启动PLC重连任务：未保存上次连接的IP地址");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "断开PLC连接过程中发生错误");
        }
    }

    private async Task ReconnectLoopAsync()
    {
        const int maxRetryInterval = 60; // 最大重试间隔60秒
        int retryCount = 0;
        
        try
        {
            Log.Information("开始重连循环，上次连接地址：{IpAddress}:{Port}", _lastIpAddress, _lastPort);
            
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    if (_lastIpAddress == null)
                    {
                        Log.Warning("无法重连：未保存上次连接的IP地址");
                        await Task.Delay(TimeSpan.FromSeconds(5), _cancellationTokenSource.Token);
                        continue;
                    }

                    Log.Information("尝试第 {RetryCount} 次重新连接PLC服务器 {IpAddress}:{Port}", 
                        retryCount + 1, _lastIpAddress, _lastPort);
                    
                    ConnectionStatusChanged?.Invoke(this, false); // 确保重连过程中状态为未连接
                    
                    await ConnectAsync(_lastIpAddress, _lastPort);
                    
                    // 如果连接成功，退出重连循环
                    if (IsConnected)
                    {
                        Log.Information("PLC服务器重连成功");
                        ConnectionStatusChanged?.Invoke(this, true);
                        // 重连成功后，也需要触发设备状态为正常
                        DeviceStatusChanged?.Invoke(this, DeviceStatusCode.Normal);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "第 {RetryCount} 次重连失败", retryCount + 1);
                    
                    // 计算下一次重试的等待时间（指数退避 + 随机抖动）
                    var baseDelay = Math.Min(Math.Pow(2, retryCount), maxRetryInterval);
                    var jitter = new Random().Next(0, 3); // 0-2秒的随机抖动
                    var delay = baseDelay + jitter;
                    
                    Log.Information("等待 {Delay} 秒后进行第 {RetryCount} 次重试", 
                        delay, retryCount + 2);
                    
                    await Task.Delay(TimeSpan.FromSeconds(delay), _cancellationTokenSource.Token);
                    retryCount++;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("重连任务被取消");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "重连过程中发生致命错误");
            ConnectionStatusChanged?.Invoke(this, false);
        }
    }

    public async Task<(bool IsSuccess, bool IsTimeout, ushort CommandId, int PackageId)> SendUploadRequestAsync(float weight, float length, float width, float height,
        string barcode1D, string barcode2D, ulong scanTimestamp)
    {
        try
        {
            var commandId = GetNextCommandId();
            var packet = new UploadRequestPacket(commandId, weight, length, width, height,
                barcode1D, barcode2D, scanTimestamp);

            // 创建一个TaskCompletionSource来等待上包结果
            var resultTcs = new TaskCompletionSource<(bool IsTimeout, int PackageId)>();
            
            // 从配置中获取超时时间
            var config = settingsService.LoadSettings<HostConfiguration>();
            var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(config.UploadTimeoutSeconds));
            // 链接外部取消令牌
            using var linkedExternalCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, _cancellationTokenSource.Token);

            // 注册一次性事件处理器
            void OnUploadResult(object? sender, (bool IsTimeout, int PackageId) result)
            {
                // 不需要检查commandId，因为流水号是PLC返回的
                resultTcs.TrySetResult(result);
            }

            UploadResultReceived += OnUploadResult;

            try
            {
                // 发送上包请求，传递取消令牌
                var response = await SendPacketAsync<UploadRequestAckPacket>(packet, linkedExternalCts.Token);
                
                Log.Information("收到上包请求响应：CommandId={CommandId}, IsAccepted={IsAccepted}", 
                    response.CommandId, 
                    response.IsAccepted);
                
                if (!response.IsAccepted)
                {
                    Log.Warning("上包请求被拒绝：CommandId={CommandId}", response.CommandId);
                    return (false, false, response.CommandId, 0);
                }

                // 等待上包结果
                // 使用链接后的令牌注册超时/取消
                await using var registration = linkedExternalCts.Token.Register(() => resultTcs.TrySetResult((true, 0)));
                var (isTimeout, packageId) = await resultTcs.Task;

                return (!isTimeout, isTimeout, response.CommandId, packageId);
            }
            finally
            {
                // 取消注册事件处理器
                UploadResultReceived -= OnUploadResult;
                timeoutCts.Dispose();
            }
        }
        catch (TimeoutException)
        {
            return (false, true, 0, 0);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "发送上包请求失败");
            return (false, false, 0, 0);
        }
    }

    public event EventHandler<DeviceStatusCode>? DeviceStatusChanged;
    public event EventHandler<(bool IsTimeout, int PackageId)>? UploadResultReceived;

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[PlcConstants.MaxPacketLength];
        var received = new List<byte>();

        try
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested && _networkStream != null)
            {
                var bytesRead = await _networkStream.ReadAsync(buffer, _cancellationTokenSource.Token);
                if (bytesRead == 0)
                {
                    // 连接已关闭
                    Log.Warning("PLC连接已关闭");
                    ConnectionStatusChanged?.Invoke(this, false); // 确保在检测到连接关闭时触发事件
                    // 触发设备状态变更事件
                    DeviceStatusChanged?.Invoke(this, DeviceStatusCode.Disconnected);
                    break;
                }

                _lastReceivedTime = DateTime.Now;
                received.AddRange(buffer.Take(bytesRead));

                // 处理接收到的数据
                ProcessReceivedData(received);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (Exception ex)
        {
            Log.Error(ex, "接收数据时发生错误");
            ConnectionStatusChanged?.Invoke(this, false); // 确保在发生异常时触发事件
            // 触发设备状态变更事件
            DeviceStatusChanged?.Invoke(this, DeviceStatusCode.Disconnected);
        }
    }

    private void ProcessReceivedData(List<byte> received)
    {
        while (received.Count >= 10) // 最小包长度
        {
            if (received[0] != PlcConstants.StartHeader1 || received[1] != PlcConstants.StartHeader2)
            {
                // 移除无效数据
                received.RemoveAt(0);
                continue;
            }

            // 获取包长度
            var length = (received[2] << 8) | received[3];
            if (received.Count < length)
                break;

            // 解析数据包
            var packetData = received.Take(length).ToArray();
            if (PlcPacket.TryParse(packetData, out var packet)) HandlePacket(packet!);

            // 移除已处理的数据
            received.RemoveRange(0, length);
        }
    }

    private void HandlePacket(PlcPacket packet)
    {

        switch (packet)
        {
            case HeartbeatAckPacket heartbeatAck:
                // 处理心跳应答
                if (_pendingRequests.TryRemove(heartbeatAck.CommandId, out var heartbeatTcs))
                {
                    heartbeatTcs.SetResult(heartbeatAck);
                }
                else
                {
                    Log.Warning("未找到等待的心跳请求：CommandId={CommandId}", heartbeatAck.CommandId);
                }
                break;

            case UploadRequestAckPacket or UploadResultAckPacket or DeviceStatusAckPacket:
                // 处理应答包
                if (_pendingRequests.TryRemove(packet.CommandId, out var tcs))
                {
                    tcs.SetResult(packet);
                }
                else
                {
                    Log.Warning("未找到等待的请求：CommandId={CommandId}, Type={Type}", 
                        packet.CommandId, 
                        packet.GetType().Name);
                }
                break;

            case UploadResultPacket uploadResult:
                // 处理上包结果
                Log.Information("收到PLC上包结果：CommandId={CommandId}, IsTimeout={IsTimeout}, PackageId={PackageId}", 
                    uploadResult.CommandId, 
                    uploadResult.IsTimeout, 
                    uploadResult.PackageId);
                
                // 触发事件，通知UI更新
                UploadResultReceived?.Invoke(this, (uploadResult.IsTimeout, uploadResult.PackageId));
                
                // 发送ACK响应
                _ = SendAckPacket(new UploadResultAckPacket(packet.CommandId), _cancellationTokenSource.Token);
                break;

            case DeviceStatusPacket deviceStatus:
                // 处理设备状态
                if (CurrentDeviceStatus != deviceStatus.StatusCode)
                {
                    CurrentDeviceStatus = deviceStatus.StatusCode;
                    DeviceStatusChanged?.Invoke(this, deviceStatus.StatusCode);
                }

                _ = SendAckPacket(new DeviceStatusAckPacket(packet.CommandId), _cancellationTokenSource.Token);
                break;
        }
    }

    private async Task HeartbeatLoopAsync()
    {
        try
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                await Task.Delay(PlcConstants.HeartbeatInterval, _cancellationTokenSource.Token);

                try
                {
                    var commandId = GetNextCommandId();
                    var packet = new HeartbeatPacket(commandId);
                    // 传递取消令牌
                    await SendPacketAsync<HeartbeatAckPacket>(packet, _cancellationTokenSource.Token);
                }
                // 捕获 OperationCanceledException 以便在取消时正常退出循环
                catch (OperationCanceledException)
                {
                    break; // 退出循环
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "发送心跳包失败");
                    ConnectionStatusChanged?.Invoke(this, false);
                    // 触发设备状态变更事件
                    DeviceStatusChanged?.Invoke(this, DeviceStatusCode.Disconnected);
                    break;
                }

                // 检查心跳超时
                if (DateTime.Now - _lastReceivedTime <= TimeSpan.FromMilliseconds(PlcConstants.HeartbeatTimeout))
                    continue;

                Log.Warning("心跳超时，准备断开连接");
                ConnectionStatusChanged?.Invoke(this, false);
                // 触发设备状态变更事件
                DeviceStatusChanged?.Invoke(this, DeviceStatusCode.Disconnected);
                break;
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (Exception ex)
        {
            Log.Error(ex, "心跳任务发生错误");
            ConnectionStatusChanged?.Invoke(this, false);
            // 触发设备状态变更事件
            DeviceStatusChanged?.Invoke(this, DeviceStatusCode.Disconnected);
        }
    }

    private async Task<T> SendPacketAsync<T>(PlcPacket packet, CancellationToken cancellationToken) where T : PlcPacket
    {
        if (_networkStream == null)
            throw new InvalidOperationException("未连接到服务器");

        const int maxRetries = 3;
        var retryCount = 0;

        while (true)
        {
            // 在每次循环开始时检查取消请求
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var tcs = new TaskCompletionSource<PlcPacket>();
                // 注册取消回调以尝试取消 TaskCompletionSource
                using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

                // 在添加请求之前检查取消，避免在取消后添加
                cancellationToken.ThrowIfCancellationRequested();
                if (!_pendingRequests.TryAdd(packet.CommandId, tcs))
                {
                     // 如果添加失败（可能因为并发或已存在），则记录警告并可能抛出异常或重试
                    Log.Warning("无法添加待处理请求，CommandId={CommandId} 可能已存在", packet.CommandId);
                    // 根据需要处理这种情况，例如抛出异常
                    throw new InvalidOperationException($"无法添加重复的待处理请求 CommandId={packet.CommandId}");
                }

                var data = packet.ToBytes();
                // 只记录非心跳包的日志
                if (packet is not HeartbeatPacket and not HeartbeatAckPacket)
                {
                    Log.Debug("发送数据包：CommandId={CommandId}, Type={Type}, Data={Data}, 重试次数={RetryCount}", 
                        packet.CommandId, 
                        packet.GetType().Name,
                        BitConverter.ToString(data).Replace("-", " "),
                        retryCount);
                }

                // 传递取消令牌给 WriteAsync 和 FlushAsync
                await _networkStream.WriteAsync(data, cancellationToken);
                await _networkStream.FlushAsync(cancellationToken);

                // 内部超时设置 - 缩短为 1 秒
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                // 链接内部超时和外部取消令牌
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);

                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(-1, linkedCts.Token));

                if (completedTask == tcs.Task)
                {
                     // 在移除请求之前再次检查取消
                    cancellationToken.ThrowIfCancellationRequested();
                    _pendingRequests.TryRemove(packet.CommandId, out _);

                    var response = await tcs.Task; // 如果 tcs.Task 被取消，这里会抛出 OperationCanceledException
                    // 只记录非心跳包的日志
                    if (response is not HeartbeatPacket and not HeartbeatAckPacket)
                    {
                        Log.Debug("收到响应包：CommandId={CommandId}, Type={Type}", 
                            response.CommandId, 
                            response.GetType().Name);
                    }

                    if (response is T typedResponse)
                        return typedResponse;

                    throw new InvalidOperationException($"收到意外的响应类型: {response.GetType().Name}");
                }

                // 超时或外部取消发生
                _pendingRequests.TryRemove(packet.CommandId, out _);

                // 检查是超时还是外部取消
                if (cancellationToken.IsCancellationRequested)
                {
                    Log.Debug("发送操作被取消：CommandId={CommandId}, Type={Type}", packet.CommandId, packet.GetType().Name);
                    throw new OperationCanceledException(cancellationToken);
                }
                // 否则是内部超时
                // 只记录非心跳包的日志
                if (packet is not HeartbeatPacket and not HeartbeatAckPacket)
                {
                    Log.Warning("等待响应超时：CommandId={CommandId}, Type={Type}, 重试次数={RetryCount}", 
                        packet.CommandId, 
                        packet.GetType().Name,
                        retryCount);
                }

                retryCount++;
                if (retryCount < maxRetries)
                {
                    // *** Corrected Retry Delay Logic ***
                    // 1. Check external cancellation *before* delaying
                    cancellationToken.ThrowIfCancellationRequested(); 
                    // 2. Use the *external* token for the delay
                    await Task.Delay(1000, cancellationToken); 
                    continue; // Continue to next retry attempt
                }

                // Max retries reached after timeout
                if (packet is HeartbeatPacket)
                {
                    DeviceStatusChanged?.Invoke(this, DeviceStatusCode.Disconnected);
                }
                throw new TimeoutException("等待响应超时");
            }
            catch (OperationCanceledException)
            {
                // If cancellation happens *before* or *during* Send/Flush or *during* WaitAsync 
                _pendingRequests.TryRemove(packet.CommandId, out _);
                Log.Debug("发送操作捕获到取消请求：CommandId={CommandId}, Type={Type}", packet.CommandId, packet.GetType().Name);
                throw; // Re-throw OperationCanceledException
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _pendingRequests.TryRemove(packet.CommandId, out _);
                if (packet is not HeartbeatPacket and not HeartbeatAckPacket)
                {
                    Log.Error(ex, "发送数据包失败：CommandId={CommandId}, Type={Type}, 重试次数={RetryCount}",
                        packet.CommandId,
                        packet.GetType().Name,
                        retryCount);
                }

                retryCount++;
                if (retryCount < maxRetries)
                {
                    // Check cancellation before delaying for retry after exception
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(1000, cancellationToken);
                }
                else 
                {
                    // Max retries reached after exception
                    if (packet is HeartbeatPacket)
                    {
                        DeviceStatusChanged?.Invoke(this, DeviceStatusCode.Disconnected);
                    }
                    throw; // Re-throw original exception or a wrapped one
                }
            }
        }
    }

    private async Task SendAckPacket(PlcPacket packet, CancellationToken cancellationToken)
    {
        const int maxRetries = 3;
        var retryCount = 0;

        while (retryCount < maxRetries)
        {
            // 检查取消请求
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (_networkStream == null)
                    return;

                var data = packet.ToBytes();
                // 传递取消令牌
                await _networkStream.WriteAsync(data, cancellationToken);
                await _networkStream.FlushAsync(cancellationToken);
                return; // 成功发送后退出
            }
             catch (OperationCanceledException)
            {
                // 如果是外部取消请求导致的，记录并重新抛出
                Log.Debug("发送ACK操作被取消：CommandId={CommandId}, Type={Type}", packet.CommandId, packet.GetType().Name);
                throw; // 重新抛出 OperationCanceledException
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error(ex, "发送ACK包失败，重试次数={RetryCount}", retryCount);
                retryCount++;
                if (retryCount < maxRetries)
                {
                    // 在重试前检查取消
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(1000, cancellationToken); // 等待1秒后重试，并传递取消令牌
                }
            }
        }
        // 如果重试次数耗尽仍失败
        Log.Error("发送ACK包最终失败，已达到最大重试次数：CommandId={CommandId}, Type={Type}",
            packet.CommandId,
            packet.GetType().Name);
    }

    private ushort GetNextCommandId()
    {
        var id = _nextCommandId;
        _nextCommandId = (ushort)(_nextCommandId == ushort.MaxValue ? 1 : _nextCommandId + 1);
        return id;
    }
}