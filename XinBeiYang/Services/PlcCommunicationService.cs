using System.Collections.Concurrent;
using Common.Services.Settings;
using Serilog;
using XinBeiYang.Models;
using XinBeiYang.Models.Communication;
using XinBeiYang.Models.Communication.Packets;
using DeviceService.DataSourceDevices.TCP; // 添加TcpClientService命名空间

namespace XinBeiYang.Services;

/// <summary>
///     PLC通讯服务实现
/// </summary>
internal class PlcCommunicationService(
    ISettingsService settingsService) : IPlcCommunicationService
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly object _connectionLock = new();
    private readonly ConcurrentDictionary<ushort, TaskCompletionSource<PlcPacket>> _pendingRequests = new();

    private readonly ConcurrentDictionary<ushort, TaskCompletionSource<(bool IsTimeout, int PackageId)>>
        _pendingUploadResults = new(); // 新增：等待最终上包结果的TCS

    private Task? _heartbeatTask;
    private bool _isDisposed;
    private DateTime _lastReceivedTime = DateTime.MinValue;
    private ushort _nextCommandId = 1;
    private bool _isStopping;
    private DeviceStatusCode CurrentDeviceStatus { get; set; } = DeviceStatusCode.Normal;

    // 替换TcpClient和NetworkStream为TcpClientService
    private TcpClientService? _tcpClientService;
    private readonly List<byte> _receivedBuffer = []; // 用于接收数据的缓冲区
    private readonly object _receivedBufferLock = new(); // 保护缓冲区的锁

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isStopping = true;

        Log.Information("正在关闭PLC通信服务...");

        // 取消所有任务
        _cancellationTokenSource.Cancel();

        // 取消所有等待的上传结果
        foreach (var tcs in _pendingUploadResults.Values)
        {
            tcs.TrySetCanceled();
        }

        _pendingUploadResults.Clear();

        // 释放TcpClientService - 它会内部处理所有网络连接、线程的关闭
        _tcpClientService?.Dispose();
        _tcpClientService = null;

        // 释放取消令牌
        _cancellationTokenSource.Dispose();

        Log.Information("PLC通信服务已关闭");
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    public event EventHandler<bool>? ConnectionStatusChanged; // 实现接口事件

    public bool IsConnected => _tcpClientService?.IsConnected() ?? false;

    public async Task ConnectAsync(string ipAddress, int port)
    {
        lock (_connectionLock)
        {
            if (_tcpClientService?.IsConnected() == true)
                return;
        }

        try
        {
            // 在创建TcpClientService前设置当前连接状态为false，以确保回调正常工作
            _tcpClientService?.Dispose();
            _tcpClientService = null;

            // 创建并初始化TcpClientService
            _tcpClientService = new TcpClientService(
                "PLC", // 设备名称
                ipAddress,
                port,
                OnDataReceived, // 数据接收回调
                OnConnectionStatusChanged // 启用自动重连
            );

            Log.Information("正在连接PLC服务器 {IpAddress}:{Port}", ipAddress, port);

            // TcpClientService.Connect是同步方法，但可能耗时，所以包装到Task.Run中
            await Task.Run(async () =>
            {
                try
                {
                    // 设置5秒连接超时
                    _tcpClientService.Connect();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "连接PLC服务器失败");
                    ConnectionStatusChanged?.Invoke(this, false); // 添加: 通知连接断开
                    // 同时触发设备状态变更事件
                    DeviceStatusChanged?.Invoke(this, DeviceStatusCode.Disconnected);
                    await DisconnectAsync();
                    throw; // 重新抛出异常以便重连循环处理
                }
            });

            // 连接成功，由TcpClientService的OnConnectionStatusChanged回调处理后续逻辑
            // 此时不需要再次调用heartbeat启动，因为会在回调中处理
            Log.Information("已连接到PLC服务器 {IpAddress}:{Port}", ipAddress, port);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "连接PLC服务器失败");
            ConnectionStatusChanged?.Invoke(this, false); // 添加: 通知连接断开
            // 同时触发设备状态变更事件
            DeviceStatusChanged?.Invoke(this, DeviceStatusCode.Disconnected);
            await DisconnectAsync();
            throw; // 重新抛出异常以便重连循环处理
        }
    }

    public Task DisconnectAsync()
    {
        // 标记为非自动断开，避免重连循环再次触发
        var wasStopping = _isStopping;
        _isStopping = true;

        try
        {
            Log.Information("开始断开PLC连接流程 (DisconnectAsync)...");

            // 1. 取消当前连接周期的任务 (如果需要精细控制，可以有单独的CTS)
            // _cancellationTokenSource.Cancel(); // 取消可能导致无法发送最后的消息，看情况

            // 2. 清理当前连接的待处理请求和结果
            ClearPendingRequestsAndResults();

            // 3. 释放TcpClientService资源 - 它会内部处理关闭TCP客户端和流
            _tcpClientService?.Dispose(); // Dispose 会触发 TcpClientService 的内部清理
            _tcpClientService = null;

            // 4. 触发状态变更
            // TcpClientService的Dispose应该触发OnConnectionStatusChanged(false)，但为确保状态更新，可以再次调用
            if (IsConnected) // 检查状态是否已更新
            {
                ConnectionStatusChanged?.Invoke(this, false); // 手动触发回调以更新状态
            }

            Log.Information("PLC连接已断开 (DisconnectAsync)");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "断开PLC连接过程中发生错误 (DisconnectAsync)");
        }
        finally
        {
            // 只有在不是全局停止时才恢复 isStopping 状态
            // 如果是 Dispose 调用的 DisconnectAsync，则 _isStopping 保持 true
            if (!wasStopping)
            {
                _isStopping = false;
            }
        }

        return Task.CompletedTask;
    }

    public event EventHandler<DeviceStatusCode>? DeviceStatusChanged;

    public event EventHandler<(ushort CommandId, bool IsTimeout, int PackageId)>? UploadResultReceived;

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
                    ConnectionStatusChanged?.Invoke(this, false); // 添加: 通知连接断开
                    // 触发设备状态变更事件
                    DeviceStatusChanged?.Invoke(this, DeviceStatusCode.Disconnected);

                    // 在发送心跳失败时主动断开连接，触发重连
                    await DisconnectAsync();
                    break;
                }

                // 检查心跳超时
                if (DateTime.Now - _lastReceivedTime <= TimeSpan.FromMilliseconds(PlcConstants.HeartbeatTimeout))
                    continue;

                Log.Warning("心跳超时，准备断开连接");
                ConnectionStatusChanged?.Invoke(this, false); // 添加: 通知连接断开
                // 触发设备状态变更事件
                DeviceStatusChanged?.Invoke(this, DeviceStatusCode.Disconnected);

                // 在心跳超时时主动断开连接，触发重连
                await DisconnectAsync();
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
            ConnectionStatusChanged?.Invoke(this, false); // 添加: 通知连接断开
            // 触发设备状态变更事件
            DeviceStatusChanged?.Invoke(this, DeviceStatusCode.Disconnected);

            // 在心跳任务异常时主动断开连接，触发重连
            await DisconnectAsync();
        }
    }

    private async Task<T> SendPacketAsync<T>(PlcPacket packet, CancellationToken cancellationToken) where T : PlcPacket
    {
        if (_tcpClientService == null || !_tcpClientService.IsConnected())
            throw new InvalidOperationException("未连接到服务器");

        // 在操作开始前检查取消请求
        cancellationToken.ThrowIfCancellationRequested();

        var tcs = new TaskCompletionSource<PlcPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

        try
        {
            // 在添加请求之前检查取消，避免在取消后添加
            cancellationToken.ThrowIfCancellationRequested();
            if (!_pendingRequests.TryAdd(packet.CommandId, tcs))
            {
                Log.Warning("无法添加待处理请求，CommandId={CommandId} 可能已存在", packet.CommandId);
                throw new InvalidOperationException($"无法添加重复的待处理请求 CommandId={packet.CommandId}");
            }

            var data = packet.ToBytes();
            // 只记录非心跳包的日志
            if (packet is not HeartbeatPacket and not HeartbeatAckPacket)
            {
                Log.Debug("发送数据包：CommandId={CommandId}, Type={Type}, Data={Data}",
                    packet.CommandId,
                    packet.GetType().Name,
                    BitConverter.ToString(data).Replace("-", " "));
            }

            // 使用TcpClientService发送数据
            await Task.Run(() =>
            {
                if (_tcpClientService == null || !_tcpClientService.IsConnected())
                    throw new InvalidOperationException("发送时连接已断开");
                _tcpClientService.Send(data);
            }, cancellationToken); // 传递取消令牌给 Task.Run

            // 直接等待 TaskCompletionSource 完成
            // 如果外部取消令牌触发 (包括超时)，注册的回调会取消 tcs，导致这里抛出 OperationCanceledException
            var response = await tcs.Task;

            // 成功收到响应
            _pendingRequests.TryRemove(packet.CommandId, out _);

            // 只记录非心跳包的日志
            if (response is not HeartbeatPacket and not HeartbeatAckPacket)
            {
                Log.Debug("收到响应包：CommandId={CommandId}, Type={Type}",
                    response.CommandId,
                    response.GetType().Name);
            }

            if (response is T typedResponse)
                return typedResponse;

            // 理论上不应发生，因为 tcs 是 PlcPacket 类型
            throw new InvalidOperationException($"收到意外的响应类型: {response.GetType().Name}");

        }
        // 捕获 OperationCanceledException，这可能由外部取消或通过注册回调触发的超时引起
        catch (OperationCanceledException)
        {
            _pendingRequests.TryRemove(packet.CommandId, out _); // 确保移除 TCS
            Log.Warning("发送/等待响应操作被取消或超时：CommandId={CommandId}, Type={Type}",
                packet.CommandId,
                packet.GetType().Name);

            // 重试次数耗尽或外部取消，直接抛出异常
            Log.Error("发送/等待响应操作超时或被取消：CommandId={CommandId}, Type={Type}",
                 packet.CommandId, packet.GetType().Name);

            if (packet is HeartbeatPacket) // 特殊处理心跳包失败
            {
                 DeviceStatusChanged?.Invoke(this, DeviceStatusCode.Disconnected);
            }
            // 重新抛出原始的 OperationCanceledException，指示调用者操作未成功
            throw;
        }
        // 捕获其他异常
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _pendingRequests.TryRemove(packet.CommandId, out _); // 确保移除 TCS
            if (packet is not HeartbeatPacket and not HeartbeatAckPacket)
            {
                Log.Error(ex, "发送数据包失败：CommandId={CommandId}, Type={Type}",
                    packet.CommandId,
                    packet.GetType().Name);
            }

            // 失败，记录错误并抛出异常
            Log.Error(ex, "发送数据包失败：CommandId={CommandId}, Type={Type}",
                packet.CommandId, packet.GetType().Name);

            if (packet is HeartbeatPacket) // 特殊处理心跳包失败
            {
                 DeviceStatusChanged?.Invoke(this, DeviceStatusCode.Disconnected);
            }

            throw; // 重新抛出异常
        }
    }

    private async Task SendAckPacket(PlcPacket packet, CancellationToken cancellationToken)
    {
        // 检查取消请求
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (_tcpClientService == null || !_tcpClientService.IsConnected())
                return; // 如果未连接，则不发送

            var data = packet.ToBytes();

            // 使用TcpClientService发送数据
            // 由于Send是同步方法，将其包装到Task.Run中
            await Task.Run(() =>
            {
                if (_tcpClientService == null || !_tcpClientService.IsConnected())
                    throw new InvalidOperationException("发送时连接已断开");
                _tcpClientService.Send(data);
            }, cancellationToken);

        }
        catch (OperationCanceledException)
        {
            // 如果是外部取消请求导致的，记录并重新抛出
            Log.Debug("发送ACK操作被取消：CommandId={CommandId}, Type={Type}", packet.CommandId, packet.GetType().Name);
            throw; // 重新抛出 OperationCanceledException
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "发送ACK包失败：CommandId={CommandId}, Type={Type}",
                packet.CommandId, packet.GetType().Name);
            // 不再重试，记录错误并重新抛出，让调用者处理
            throw;
        }
    }

    private ushort GetNextCommandId()
    {
        var id = _nextCommandId;
        _nextCommandId = (ushort)(_nextCommandId == ushort.MaxValue ? 1 : _nextCommandId + 1);
        return id;
    }

    // 处理连接状态变更的回调
    private void OnConnectionStatusChanged(bool isConnected)
    {
        if (_isDisposed) return;

        // Always invoke the boolean status change event
        ConnectionStatusChanged?.Invoke(this, isConnected);

        if (isConnected)
        {
            _lastReceivedTime = DateTime.Now;
            Log.Information("PLC连接成功。之前的状态为: {PreviousStatus}", CurrentDeviceStatus);
            CurrentDeviceStatus = DeviceStatusCode.Normal;
            DeviceStatusChanged?.Invoke(this, DeviceStatusCode.Normal);
            Log.Information("触发 DeviceStatusChanged 事件，状态: Normal");


            // Start heartbeat if not running
            if (_heartbeatTask is { IsCompleted: false }) return;
            _heartbeatTask = Task.Run(HeartbeatLoopAsync, _cancellationTokenSource.Token);
            Log.Debug("PLC心跳任务已启动");
        }
        else // isConnected is false
        {
            // Only trigger Disconnected if the status wasn't already Disconnected
            if (CurrentDeviceStatus != DeviceStatusCode.Disconnected)
            {
                Log.Information("PLC连接断开，设置状态为 Disconnected。");
                CurrentDeviceStatus = DeviceStatusCode.Disconnected; // Update internal state
                DeviceStatusChanged?.Invoke(this, DeviceStatusCode.Disconnected); // Trigger ViewModel 更新
            }
            else
            {
                Log.Debug("PLC连接状态仍为断开，无需重复触发事件。");
            }

            // Cleanup
            ClearPendingRequestsAndResults();
        }
    }

    // 清理等待的请求和结果
    private void ClearPendingRequestsAndResults()
    {
        // 清理等待 ACK 的请求
        foreach (var kvp in _pendingRequests)
        {
            kvp.Value.TrySetCanceled();
        }

        _pendingRequests.Clear();

        // 清理等待最终上传结果的请求
        foreach (var kvp in _pendingUploadResults)
        {
            kvp.Value.TrySetResult((true, 0)); // 设置为超时结果
        }

        _pendingUploadResults.Clear();
        Log.Debug("已清理所有待处理的PLC请求和上传结果（标记为超时）。");
    }

    // 处理接收到数据的回调
    private void OnDataReceived(byte[] data)
    {
        if (_isDisposed) return;

        _lastReceivedTime = DateTime.Now;

        lock (_receivedBufferLock)
        {
            _receivedBuffer.AddRange(data);
            ProcessReceivedBuffer();
        }
    }

    // 处理接收缓冲区中的数据
    private void ProcessReceivedBuffer()
    {
        while (_receivedBuffer.Count >= 10) // 最小包长度
        {
            if (_receivedBuffer[0] != PlcConstants.StartHeader1 || _receivedBuffer[1] != PlcConstants.StartHeader2)
            {
                // 移除无效数据
                _receivedBuffer.RemoveAt(0);
                continue;
            }

            // 获取包长度
            var length = (_receivedBuffer[2] << 8) | _receivedBuffer[3];
            if (_receivedBuffer.Count < length)
                break;

            // 解析数据包
            var packetData = _receivedBuffer.Take(length).ToArray();
            if (PlcPacket.TryParse(packetData, out var packet))
            {
                // 创建副本并在单独的任务中处理，避免阻塞接收线程
                var packetCopy = packet;
                Task.Run(() => HandlePacket(packetCopy!));
            }

            // 移除已处理的数据
            _receivedBuffer.RemoveRange(0, length);
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
                    tcs.TrySetResult(packet);
                }
                else
                {
                    // UploadRequestAckPacket 的 Tcs 是在 SendPacketAsync 内部处理的，不应在这里移除
                    // 这里只应该处理 UploadResultAckPacket 和 DeviceStatusAckPacket （如果SendAckPacket等待的话）
                    // 实际上，我们并不等待 ACK 包的 ACK，所以这里可能只需要记录日志
                    if (packet is not UploadRequestAckPacket)
                    {
                        Log.Warning("收到未找到对应请求的ACK包：CommandId={CommandId}, Type={Type}",
                            packet.CommandId,
                            packet.GetType().Name);
                    }
                }

                break;

            case UploadResultPacket uploadResult:
                // 处理上包结果
                Log.Information("收到PLC上包结果：CommandId={CommandId}, IsTimeout={IsTimeout}, PackageId={PackageId}",
                    uploadResult.CommandId,
                    uploadResult.IsTimeout,
                    uploadResult.PackageId);

                // *** 新增: 触发上包最终结果事件 ***
                try
                {
                    UploadResultReceived?.Invoke(this, (uploadResult.CommandId, uploadResult.IsTimeout, uploadResult.PackageId));
                    Log.Debug("已触发 UploadResultReceived 事件: CommandId={CommandId}", uploadResult.CommandId);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "触发 UploadResultReceived 事件时发生错误: CommandId={CommandId}", uploadResult.CommandId);
                }

                // 找到对应的 TaskCompletionSource 并设置结果（保持向后兼容）
                if (_pendingUploadResults.TryGetValue(uploadResult.CommandId, out var resultTcs))
                {
                    // TrySetResult returns false if the task was already completed (e.g., by timeout/cancellation)
                    if (resultTcs.TrySetResult((uploadResult.IsTimeout, uploadResult.PackageId)))
                    {
                        Log.Debug("为 CommandId={CommandId} 设置了最终上包结果.", uploadResult.CommandId);
                    }
                    else
                    {
                        Log.Warning("尝试为 CommandId={CommandId} 设置最终上包结果失败，TCS 可能已完成 (例如超时).", uploadResult.CommandId);
                    }
                }
                else
                {
                    Log.Debug("收到 CommandId={CommandId} 的上包结果，但未找到对应的等待 Tcs（可能使用新的异步模式）。", uploadResult.CommandId);
                }

                // 发送ACK响应
                _ = SendAckPacket(new UploadResultAckPacket(packet.CommandId), _cancellationTokenSource.Token);
                break;

            case DeviceStatusPacket deviceStatus:
                // 处理设备状态
                if (CurrentDeviceStatus != deviceStatus.StatusCode ||
                    !IsConnected) // Also update if status changes or connection just established
                {
                    // Update local status first
                    CurrentDeviceStatus = deviceStatus.StatusCode;
                    // Then invoke the event
                    DeviceStatusChanged?.Invoke(this, deviceStatus.StatusCode);
                    Log.Information("PLC设备状态更新: {Status}", deviceStatus.StatusCode);
                }

                _ = SendAckPacket(new DeviceStatusAckPacket(packet.CommandId), _cancellationTokenSource.Token);
                break;
        }
    }

    // 修改 SendUploadRequestAsync: 只等待初始ACK (实现接口)
    public async Task<(bool IsAccepted, ushort CommandId)> SendUploadRequestAsync(float weight, float length,
        float width, float height,
        string barcode1D, string barcode2D, ulong scanTimestamp, CancellationToken cancellationToken)
    {
        ushort commandId = 0; // 初始化 commandId
        try
        {
            commandId = GetNextCommandId();
            var packet = new UploadRequestPacket(commandId, weight, length, width, height,
                barcode1D, barcode2D, scanTimestamp);

            // 从配置中获取超时时间 (用于初始ACK)
            var config = settingsService.LoadSettings<HostConfiguration>();
            // 使用新的 UploadAckTimeoutSeconds 作为 ACK 超时时间
            var ackTimeoutSeconds =
                config.UploadAckTimeoutSeconds > 0 ? config.UploadAckTimeoutSeconds : 10; // 默认10秒
            using var ackTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(ackTimeoutSeconds));
            // 链接外部取消令牌和ACK超时令牌
            using var linkedAckCts =
                CancellationTokenSource.CreateLinkedTokenSource(ackTimeoutCts.Token, cancellationToken);

            // 发送上包请求，等待初始 ACK
            var response = await SendPacketAsync<UploadRequestAckPacket>(packet, linkedAckCts.Token);

            Log.Information("收到上包请求ACK：CommandId={CommandId}, IsAccepted={IsAccepted}",
                response.CommandId,
                response.IsAccepted);

            // 如果PLC接受了请求，为最终结果创建一个 TaskCompletionSource
            if (response.IsAccepted)
            {
                var resultTcs =
                    new TaskCompletionSource<(bool IsTimeout, int PackageId)>(TaskCreationOptions
                        .RunContinuationsAsynchronously);
                if (!_pendingUploadResults.TryAdd(response.CommandId, resultTcs))
                {
                    Log.Error("无法为 CommandId={CommandId} 添加待处理的上包结果 Tcs，可能已存在。", response.CommandId);
                    // 标记为拒绝，因为无法跟踪最终结果
                    return (false, response.CommandId);
                }

                Log.Debug("为 CommandId={CommandId} 添加了等待最终上包结果的 Tcs。", response.CommandId);
            }

            return (response.IsAccepted, response.CommandId);
        }
        // TimeoutException specifically for the ACK wait
        catch (TimeoutException ackTimeoutEx)
        {
            Log.Error(ackTimeoutEx, "等待上包请求ACK超时: CommandId={CommandId}", commandId);
            return (false, commandId); // Return not accepted on ACK timeout
        }
        catch (OperationCanceledException opCancelEx) // Catch cancellation during ACK wait
        {
            // Don't log error if cancellation was requested externally
            if (cancellationToken.IsCancellationRequested)
            {
                Log.Information("发送上包请求或等待ACK时操作被外部取消: CommandId={CommandId}", commandId);
            }
            else
            {
                Log.Warning(opCancelEx, "发送上包请求或等待ACK时操作被取消(非外部): CommandId={CommandId}", commandId);
            }

            return (false, commandId); // Return not accepted if cancelled
        }
        catch (Exception ex)
        {
            Log.Error(ex, "发送上包请求或等待ACK时失败: CommandId={CommandId}", commandId);
            return (false, commandId); // Return not accepted on general failure during this phase
        }
    }

    // 新增方法：等待最终的上包结果 (实现接口)
    public async Task<(bool WasSuccess, bool IsTimeout, int PackageId)> WaitForUploadResultAsync(ushort commandId,
        CancellationToken cancellationToken)
    {
        if (!_pendingUploadResults.TryGetValue(commandId, out var tcs))
        {
            Log.Warning("WaitForUploadResultAsync: 未找到 CommandId={CommandId} 的待处理上包结果 Tcs。", commandId);
            // 返回一个表示未找到或失败的状态
            return (false, false, 0);
        }

        try
        {
            // 从配置获取最终结果的超时时间
            var config = settingsService.LoadSettings<HostConfiguration>();
            // 使用新的 UploadResultTimeoutSeconds 作为最终结果超时时间
            var timeoutSeconds = config.UploadResultTimeoutSeconds > 0 ? config.UploadResultTimeoutSeconds : 60; // 默认60秒
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            // 链接外部取消令牌和最终结果超时令牌
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

            // 使用链接后的令牌注册超时/取消回调
            // 注意：这里直接设置结果为超时 (true, 0)
            await using var registration = linkedCts.Token.Register(() =>
            {
                if (tcs.TrySetResult((true, 0)))
                {
                    // Set timeout result
                    Log.Warning("等待 CommandId={CommandId} 的最终上包结果超时或被取消。", commandId);
                }
            });

            // 等待 TaskCompletionSource 完成 (由 HandlePacket 或上面的注册回调完成)
            var (isTimeout, packageId) = await tcs.Task;

            Log.Debug(
                "WaitForUploadResultAsync 完成: CommandId={CommandId}, IsTimeout={IsTimeout}, PackageId={PackageId}",
                commandId, isTimeout, packageId);
            return (!isTimeout && packageId > 0, isTimeout, packageId); // WasSuccess is !isTimeout AND packageId > 0
        }
        catch (TaskCanceledException) // Catch if tcs.Task itself was cancelled externally before await
        {
            Log.Warning("等待 CommandId={CommandId} 的最终上包结果时任务被取消(TaskCanceledException)。", commandId);
            return (false, true, 0); // Treat cancellation as timeout
        }
        catch (Exception ex)
        {
            Log.Error(ex, "等待 CommandId={CommandId} 的最终上包结果时出错。", commandId);
            return (false, false, 0); // Indicate error
        }
        finally
        {
            // 无论结果如何，都尝试移除 TCS
            if (_pendingUploadResults.TryRemove(commandId, out _))
            {
                Log.Debug("已移除 CommandId={CommandId} 的待处理上包结果 Tcs。", commandId);
            }
        }
    }
}