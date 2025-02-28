using System.Collections.Concurrent;
using System.Net.Sockets;
using Presentation_CommonLibrary.Services;
using Presentation_XinBeiYang.Models.Communication;
using Presentation_XinBeiYang.Models.Communication.Packets;
using Serilog;

namespace Presentation_XinBeiYang.Services;

/// <summary>
/// PLC通讯服务实现
/// </summary>
public class PlcCommunicationService(INotificationService notificationService) : IPlcCommunicationService, IDisposable
{
    private readonly ConcurrentDictionary<ushort, TaskCompletionSource<PlcPacket>> _pendingRequests = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly object _connectionLock = new();
    private TcpClient? _tcpClient;
    private NetworkStream? _networkStream;
    private Task? _receiveTask;
    private Task? _heartbeatTask;
    private ushort _nextCommandId = 1;
    private bool _isDisposed;
    private DateTime _lastReceivedTime = DateTime.MinValue;

    public event EventHandler<bool>? ConnectionStatusChanged;
    public event EventHandler<DeviceStatusCode>? DeviceStatusChanged;
    public event EventHandler<(bool IsTimeout, int PackageId)>? UploadResultReceived;

    public bool IsConnected => _tcpClient?.Connected ?? false;
    public DeviceStatusCode CurrentDeviceStatus { get; private set; } = DeviceStatusCode.Normal;

    public async Task ConnectAsync(string ipAddress, int port)
    {
        lock (_connectionLock)
        {
            if (_tcpClient?.Connected == true)
                return;
        }

        try
        {
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(ipAddress, port);
            _networkStream = _tcpClient.GetStream();

            // 启动接收任务
            _receiveTask = Task.Run(ReceiveLoopAsync);

            // 启动心跳任务
            _heartbeatTask = Task.Run(HeartbeatLoopAsync);

            ConnectionStatusChanged?.Invoke(this, true);
            Log.Information("已连接到PLC服务器 {IpAddress}:{Port}", ipAddress, port);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "连接PLC服务器失败");
            notificationService.ShowError("连接失败", ex.Message);
            await DisconnectAsync();
        }
    }

    public async Task DisconnectAsync()
    {
        try
        {
            await _cancellationTokenSource.CancelAsync();

            if (_receiveTask != null)
                await _receiveTask;

            if (_heartbeatTask != null)
                await _heartbeatTask;

            lock (_connectionLock)
            {
                _networkStream?.Dispose();
                _tcpClient?.Dispose();
                _networkStream = null;
                _tcpClient = null;
            }

            ConnectionStatusChanged?.Invoke(this, false);
            Log.Information("已断开与PLC服务器的连接");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "断开连接时发生错误");
        }
    }

    public async Task<bool> SendUploadRequestAsync(float weight, float length, float width, float height,
        string barcode1D, string barcode2D, ulong scanTimestamp)
    {
        try
        {
            var commandId = GetNextCommandId();
            var packet = new UploadRequestPacket(commandId, weight, length, width, height,
                barcode1D, barcode2D, scanTimestamp);

            var response = await SendPacketAsync<UploadRequestAckPacket>(packet);
            return response.IsAccepted;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "发送上包请求失败");
            notificationService.ShowError("发送失败", ex.Message);
            return false;
        }
    }

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
            if (PlcPacket.TryParse(packetData, out var packet))
            {
                HandlePacket(packet!);
            }

            // 移除已处理的数据
            received.RemoveRange(0, length);
        }
    }

    private void HandlePacket(PlcPacket packet)
    {
        switch (packet)
        {
            case HeartbeatAckPacket:
                // 心跳应答，不需要特殊处理
                break;

            case UploadRequestAckPacket or UploadResultAckPacket or DeviceStatusAckPacket:
                // 处理应答包
                if (_pendingRequests.TryRemove(packet.CommandId, out var tcs))
                {
                    tcs.SetResult(packet);
                }
                break;

            case UploadResultPacket uploadResult:
                // 处理上包结果
                UploadResultReceived?.Invoke(this, (uploadResult.IsTimeout, uploadResult.PackageId));
                SendAckPacket(new UploadResultAckPacket(packet.CommandId));
                break;

            case DeviceStatusPacket deviceStatus:
                // 处理设备状态
                if (CurrentDeviceStatus != deviceStatus.StatusCode)
                {
                    CurrentDeviceStatus = deviceStatus.StatusCode;
                    DeviceStatusChanged?.Invoke(this, deviceStatus.StatusCode);
                }
                SendAckPacket(new DeviceStatusAckPacket(packet.CommandId));
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
                    await DisconnectAsync();
                    break;
                }

                // 检查心跳超时
                if (DateTime.Now - _lastReceivedTime <=
                    TimeSpan.FromMilliseconds(PlcConstants.HeartbeatTimeout)) continue;
                Log.Warning("心跳超时");
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
            await DisconnectAsync();
        }
    }

    private async Task<T> SendPacketAsync<T>(PlcPacket packet) where T : PlcPacket
    {
        if (_networkStream == null)
            throw new InvalidOperationException("未连接到服务器");

        var tcs = new TaskCompletionSource<PlcPacket>();
        _pendingRequests[packet.CommandId] = tcs;

        try
        {
            var data = packet.ToBytes();
            await _networkStream.WriteAsync(data);
            await _networkStream.FlushAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(-1, cts.Token));

            if (completedTask == tcs.Task)
            {
                var response = await tcs.Task;
                if (response is T typedResponse)
                    return typedResponse;
                throw new InvalidOperationException($"收到意外的响应类型: {response.GetType().Name}");
            }

            _pendingRequests.TryRemove(packet.CommandId, out _);
            throw new TimeoutException("等待响应超时");
        }
        catch (Exception)
        {
            _pendingRequests.TryRemove(packet.CommandId, out _);
            throw;
        }
    }

    private async void SendAckPacket(PlcPacket packet)
    {
        try
        {
            if (_networkStream == null)
                return;

            var data = packet.ToBytes();
            await _networkStream.WriteAsync(data);
            await _networkStream.FlushAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "发送ACK包失败");
        }
    }

    private ushort GetNextCommandId()
    {
        var id = _nextCommandId;
        _nextCommandId = (ushort)(_nextCommandId == ushort.MaxValue ? 1 : _nextCommandId + 1);
        return id;
    }

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
} 