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
    private DeviceStatusCode CurrentDeviceStatus { get; set; } = DeviceStatusCode.Normal;

    public void Dispose()
    {
        if (_isDisposed)
            return;

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

            // 启动接收任务
            _receiveTask = Task.Run(ReceiveLoopAsync, timeoutCts.Token);

            // 启动心跳任务
            _heartbeatTask = Task.Run(HeartbeatLoopAsync, timeoutCts.Token);

            _lastReceivedTime = DateTime.Now; // 更新最后接收时间
            ConnectionStatusChanged?.Invoke(this, true);
            Log.Information("已连接到PLC服务器 {IpAddress}:{Port}", ipAddress, port);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "连接PLC服务器失败");
            ConnectionStatusChanged?.Invoke(this, false); // 确保连接失败时也触发事件
            await DisconnectAsync();
            throw; // 重新抛出异常以便重连循环处理
        }
    }

    public async Task DisconnectAsync()
    {
        try
        {
            Log.Information("开始断开连接流程...");

            // 先取消所有任务
            await _cancellationTokenSource.CancelAsync();

            // 等待所有任务完成
            var tasks = new List<Task>();
            
            if (_reconnectTask != null)
            {
                tasks.Add(_reconnectTask);
            }
            
            if (_receiveTask != null)
            {
                tasks.Add(_receiveTask);
            }
            
            if (_heartbeatTask != null)
            {
                tasks.Add(_heartbeatTask);
            }

            // 等待所有任务完成，设置超时
            if (tasks.Count > 0)
            {
                try
                {
                    await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException)
                {
                    Log.Warning("等待任务完成超时");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "等待任务完成时发生错误");
                }
            }

            // 清理资源
            lock (_connectionLock)
            {
                _networkStream?.Dispose();
                _tcpClient?.Dispose();
                _networkStream = null;
                _tcpClient = null;
            }

            ConnectionStatusChanged?.Invoke(this, false);
            Log.Information("已断开与PLC服务器的连接");

            // 创建新的取消令牌源
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();

            // 启动重连任务
            if (_lastIpAddress != null)
            {
                _reconnectTask = Task.Run(ReconnectLoopAsync);
                Log.Information("已启动重连任务");
            }
            else
            {
                Log.Warning("无法启动重连任务：未保存上次连接的IP地址");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "断开连接时发生错误");
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

            // 注册一次性事件处理器
            void OnUploadResult(object? sender, (bool IsTimeout, int PackageId) result)
            {
                // 不需要检查commandId，因为流水号是PLC返回的
                resultTcs.TrySetResult(result);
            }

            UploadResultReceived += OnUploadResult;

            try
            {
                // 发送上包请求
                var response = await SendPacketAsync<UploadRequestAckPacket>(packet);
                
                Log.Information("收到上包请求响应：CommandId={CommandId}, IsAccepted={IsAccepted}", 
                    response.CommandId, 
                    response.IsAccepted);
                
                if (!response.IsAccepted)
                {
                    Log.Warning("上包请求被拒绝：CommandId={CommandId}", response.CommandId);
                    return (false, false, response.CommandId, 0);
                }

                // 等待上包结果
                await using var registration = timeoutCts.Token.Register(() => resultTcs.TrySetResult((true, 0)));
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
                    await DisconnectAsync();
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
            await DisconnectAsync();
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
        Log.Debug("处理数据包：CommandId={CommandId}, Type={Type}", 
            packet.CommandId, 
            packet.GetType().Name);

        switch (packet)
        {
            case HeartbeatAckPacket heartbeatAck:
                // 处理心跳应答
                if (_pendingRequests.TryRemove(heartbeatAck.CommandId, out var heartbeatTcs))
                {
                    Log.Debug("设置心跳响应结果：CommandId={CommandId}", heartbeatAck.CommandId);
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
                    Log.Debug("设置响应结果：CommandId={CommandId}, Type={Type}", 
                        packet.CommandId, 
                        packet.GetType().Name);
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
                _ = SendAckPacket(new UploadResultAckPacket(packet.CommandId));
                break;

            case DeviceStatusPacket deviceStatus:
                // 处理设备状态
                if (CurrentDeviceStatus != deviceStatus.StatusCode)
                {
                    CurrentDeviceStatus = deviceStatus.StatusCode;
                    DeviceStatusChanged?.Invoke(this, deviceStatus.StatusCode);
                }

                _ = SendAckPacket(new DeviceStatusAckPacket(packet.CommandId));
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
                    await SendPacketAsync<HeartbeatAckPacket>(packet);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "发送心跳包失败");
                    ConnectionStatusChanged?.Invoke(this, false);
                    await DisconnectAsync();
                    break;
                }

                // 检查心跳超时
                if (DateTime.Now - _lastReceivedTime <= TimeSpan.FromMilliseconds(PlcConstants.HeartbeatTimeout))
                    continue;

                Log.Warning("心跳超时，准备断开连接");
                ConnectionStatusChanged?.Invoke(this, false);
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
            ConnectionStatusChanged?.Invoke(this, false);
            await DisconnectAsync();
        }
    }

    private async Task<T> SendPacketAsync<T>(PlcPacket packet) where T : PlcPacket
    {
        if (_networkStream == null)
            throw new InvalidOperationException("未连接到服务器");

        const int maxRetries = 3;
        var retryCount = 0;

        while (true)
        {
            try
            {
                var tcs = new TaskCompletionSource<PlcPacket>();
                _pendingRequests[packet.CommandId] = tcs;

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

                await _networkStream.WriteAsync(data);
                await _networkStream.FlushAsync();

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(-1, cts.Token));

                if (completedTask == tcs.Task)
                {
                    var response = await tcs.Task;
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

                _pendingRequests.TryRemove(packet.CommandId, out _);
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
                    await Task.Delay(1000, cts.Token); // 等待1秒后重试
                    continue;
                }

                throw new TimeoutException("等待响应超时");
            }
            catch (Exception ex)
            {
                _pendingRequests.TryRemove(packet.CommandId, out _);
                // 只记录非心跳包的日志
                if (packet is not HeartbeatPacket and not HeartbeatAckPacket)
                {
                    Log.Error(ex, "发送数据包失败：CommandId={CommandId}, Type={Type}, 重试次数={RetryCount}", 
                        packet.CommandId, 
                        packet.GetType().Name,
                        retryCount);
                }

                retryCount++;
                if (retryCount >= maxRetries) throw;
                await Task.Delay(1000); // 等待1秒后重试
            }
        }
    }

    private async Task SendAckPacket(PlcPacket packet)
    {
        const int maxRetries = 3;
        var retryCount = 0;

        while (retryCount < maxRetries)
        {
            try
            {
                if (_networkStream == null)
                    return;

                var data = packet.ToBytes();
                await _networkStream.WriteAsync(data);
                await _networkStream.FlushAsync();
                return;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "发送ACK包失败，重试次数={RetryCount}", retryCount);
                retryCount++;
                if (retryCount < maxRetries)
                {
                    await Task.Delay(1000); // 等待1秒后重试
                }
            }
        }
    }

    private ushort GetNextCommandId()
    {
        var id = _nextCommandId;
        _nextCommandId = (ushort)(_nextCommandId == ushort.MaxValue ? 1 : _nextCommandId + 1);
        return id;
    }
}