using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Windows.Media.Imaging;
using Common.Models.Package;
using Serilog;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;

namespace Camera.Services.Implementations.TCP;

/// <summary>
///     TCP相机服务实现 (已重构为作为TCP服务端)
/// </summary>
public class TcpCameraService : IDisposable
{
    private const int MaxBufferSize = 1024 * 1024; // 最大缓冲区大小（1MB）

    private readonly Subject<PackageInfo> _packageSubject = new();
    private readonly Subject<BitmapSource> _realtimeImageSubject = new(); // 保留，但当前未使用

    private readonly int _port;
    private TcpListener _listener;
    private CancellationTokenSource _cancellationTokenSource;
    private Task _listenTask;
    private readonly List<TcpClient> _connectedClients = [];
    private readonly Dictionary<TcpClient, StringBuilder> _clientBuffers = [];
    private readonly object _clientsLock = new();
    private readonly string _host;

    public TcpCameraService(string host = "127.0.0.1", int port = 20011)
    {
        _host = host;
        _port = port;
        Log.Information("TCP相机服务已创建，将在 {Host}:{Port} 上监听连接", _host, _port);
    }

    public bool IsConnected
    {
        get
        {
            lock (_clientsLock)
            {
                return _connectedClients.Count > 0;
            }
        }
    }

    public IObservable<PackageInfo> PackageStream => _packageSubject.AsObservable();

    public event Action<string, bool>? ConnectionChanged;

    /// <summary>
    /// 释放资源，停止服务器并断开所有连接。
    /// </summary>
    public void Dispose()
    {
        Log.Debug("正在 Dispose TCP相机服务 (TcpCameraService)...");
        Stop();
        _packageSubject.Dispose();
        _realtimeImageSubject.Dispose();
        Log.Debug("TCP相机服务 (TcpCameraService) Dispose 完成");
    }

    public bool Start()
    {
        try
        {
            Log.Information("正在启动 TCP相机服务 (启动TCP监听)...");
            _cancellationTokenSource = new CancellationTokenSource();
            if (!IPAddress.TryParse(_host, out var ipAddress))
            {
                Log.Error("无效的IP地址: {Host}，将使用 IPAddress.Any", _host);
                ipAddress = IPAddress.Any;
            }
            _listener = new TcpListener(ipAddress, _port);
            _listener.Start();
            _listenTask = ListenForClientsAsync(_cancellationTokenSource.Token);
            Log.Information("TCP相机服务已在 {IPAddress}:{Port} 上开始监听", _listener.LocalEndpoint, _port);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动 TCP相机服务失败");
            return false;
        }
    }

    public bool Stop()
    {
        try
        {
            Log.Information("正在停止 TCP相机服务...");
            if (_cancellationTokenSource == null || _cancellationTokenSource.IsCancellationRequested)
            {
                return true;
            }

            _cancellationTokenSource.Cancel();
            _listener.Stop();

            lock (_clientsLock)
            {
                foreach (var client in _connectedClients)
                {
                    client.Dispose();
                }
                _connectedClients.Clear();
                _clientBuffers.Clear();
            }

            _listenTask?.Wait(TimeSpan.FromSeconds(2)); // 等待监听任务结束

            Log.Information("TCP相机服务已停止");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止 TCP相机服务时发生错误");
            return false;
        }
    }

    private async Task ListenForClientsAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(token);
                var clientEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "未知客户端";
                Log.Information("接受客户端连接: {ClientEndPoint}", clientEndPoint);

                lock (_clientsLock)
                {
                    _connectedClients.Add(client);
                    _clientBuffers.Add(client, new StringBuilder());
                    ConnectionChanged?.Invoke(clientEndPoint, true);
                }

                // 为每个客户端启动一个处理任务
                _ = HandleClientAsync(client, token);
            }
        }
        catch (OperationCanceledException)
        {
            // 这是预期的异常，当取消令牌被触发时
            Log.Debug("TCP监听任务已取消。");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "监听客户端连接时发生异常");
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        var clientEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "未知客户端";
        var stream = client.GetStream();
        var buffer = new byte[4096];

        try
        {
            while (!token.IsCancellationRequested)
            {
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token);
                if (bytesRead == 0)
                {
                    Log.Information("客户端 {ClientEndPoint} 断开连接 (流关闭)", clientEndPoint);
                    break; // 客户端主动断开
                }
                HandleDataReceived(client, buffer, bytesRead);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Debug("客户端 {ClientEndPoint} 的处理任务被取消。", clientEndPoint);
        }
        catch (IOException ex) when (ex.InnerException is SocketException)
        {
            Log.Warning("与客户端 {ClientEndPoint} 的连接丢失 (IO异常): {ErrorMessage}", clientEndPoint, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理客户端 {ClientEndPoint} 数据时发生错误", clientEndPoint);
        }
        finally
        {
            lock (_clientsLock)
            {
                client.Dispose();
                _connectedClients.Remove(client);
                _clientBuffers.Remove(client);
                ConnectionChanged?.Invoke(clientEndPoint, false);
            }
            Log.Information("客户端 {ClientEndPoint} 的资源已清理", clientEndPoint);
        }
    }

    private void HandleDataReceived(TcpClient client, byte[] data, int bytesRead)
    {
        try
        {
            var receivedString = Encoding.UTF8.GetString(data, 0, bytesRead);
            Log.Debug("TcpCameraService - 从 {ClientEndPoint} 收到原始数据片段: {Data}", client.Client.RemoteEndPoint, receivedString);

            StringBuilder clientBuffer;
            lock (_clientsLock)
            {
                if (!_clientBuffers.TryGetValue(client, out clientBuffer))
                {
                    Log.Warning("无法为客户端 {ClientEndPoint} 找到缓冲区, 忽略此数据", client.Client.RemoteEndPoint);
                    return;
                }
            }

            List<string> packetsToProcess;
            lock (clientBuffer)
            {
                clientBuffer.Append(receivedString);

                // 检查缓冲区大小
                if (clientBuffer.Length > MaxBufferSize)
                {
                    Log.Warning("客户端 {ClientEndPoint} 的接收缓冲区大小超过限制 ({Size} > {MaxSize})，清空缓冲区",
                        client.Client.RemoteEndPoint, clientBuffer.Length, MaxBufferSize);
                    clientBuffer.Clear();
                    return;
                }

                // 提取所有完整的数据包
                var (extractedPackets, remainder) = ExtractAllValidPackets(clientBuffer.ToString());
                packetsToProcess = extractedPackets;

                // 更新缓冲区，只保留剩余不完整的部分
                if (clientBuffer.ToString() != remainder)
                {
                    clientBuffer.Clear().Append(remainder);
                }
            }

            // 在锁外部，串行处理提取出的数据包
            foreach (var packet in packetsToProcess)
            {
                // 直接调用，保证顺序处理，避免并发问题
                ProcessPackageData(packet);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理接收到的TCP数据时发生错误");
            // 发生错误时考虑清空缓冲区，防止错误数据导致后续处理持续失败
            StringBuilder clientBuffer;
            lock (_clientsLock)
            {
                if (_clientBuffers.TryGetValue(client, out clientBuffer))
                {
                    lock (clientBuffer)
                    {
                        clientBuffer.Clear();
                    }
                }
            }
        }
    }

    private static (List<string> Packets, string Remainder) ExtractAllValidPackets(string bufferContent)
    {
        var packets = new List<string>();
        var currentContent = bufferContent;
        while (true)
        {
            var (packet, processedLength) = ExtractFirstValidPacket(currentContent);
            if (packet != null)
            {
                packets.Add(packet);
                currentContent = currentContent[processedLength..];
            }
            else
            {
                // 没有更多完整的数据包了
                break;
            }
        }
        return (packets, currentContent);
    }

    // Validates a packet according to the new 8-part structure.
    private static bool ValidatePacket(List<string> packetParts)
    {
        if (packetParts.Count != 8) return false; // Strictly 8 parts

        // part 0: guid (string, non-empty)
        if (string.IsNullOrEmpty(packetParts[0].Trim())) return false;

        // part 1: code (string, non-empty) - "noread" is a value, not a format failure here.
        if (string.IsNullOrEmpty(packetParts[1].Trim())) return false;

        // part 2: weight (float)
        if (!float.TryParse(packetParts[2], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _)) return false;
        // part 3: length (double)
        if (!double.TryParse(packetParts[3], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _)) return false;
        // part 4: width (double)
        if (!double.TryParse(packetParts[4], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _)) return false;
        // part 5: height (double)
        if (!double.TryParse(packetParts[5], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _)) return false;
        // part 6: volume (double)
        if (!double.TryParse(packetParts[6], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _)) return false;
        // part 7: timestamp (long)
        if (!long.TryParse(packetParts[7], out _)) return false;

        return true;
    }

    // Extracts the first valid packet, now delimited by '@'.
    private static (string? Packet, int ProcessedLength) ExtractFirstValidPacket(string bufferContent)
    {
        int endOfPacketIndex = bufferContent.IndexOf('@');

        if (endOfPacketIndex == -1)
        {
            // No '@' delimiter found, so no complete packet in the buffer yet.
            Log.Debug("ExtractFirstValidPacket: No '@' delimiter found. Buffer: '{Buffer}'", bufferContent);
            return (null, 0); // Processed 0 bytes as no delimiter was found.
        }

        // The packet data is the content before the '@' delimiter.
        string potentialPacketData = bufferContent[..endOfPacketIndex];
        // We will consume data up to and including the '@' delimiter.
        int processedLength = endOfPacketIndex + 1;

        // Validate the internal structure of the potential packet data.
        // It should consist of 8 comma-separated parts that are subsequently trimmed.
        var partsList = potentialPacketData.Split(',').Select(s => s.Trim()).ToList();

        if (ValidatePacket(partsList)) // ValidatePacket checks for 8 valid parts.
        {
            Log.Debug("Found valid packet (terminated by '@'): '{PacketData}'. Processed Length: {Length}", potentialPacketData, processedLength);
            // Return the raw packet data string; ProcessPackageData will handle splitting and parsing.
            return (potentialPacketData, processedLength);
        }
        
        // If validation fails, the segment up to and including '@' is still consumed.
        // Log this occurrence.
        Log.Warning("ExtractFirstValidPacket: Invalid packet data ('{PotentialData}') before '@' delimiter. Expected 8 comma-separated parts. Parts found: [{ActualParts}]. Discarding this segment (length {Length}).",
            potentialPacketData, string.Join(" | ", partsList), processedLength);
        return (null, processedLength); // Packet is invalid, but the segment is consumed to prevent reprocessing.
    }

    private void ProcessPackageData(string packetData)
    {
        try
        {
            Log.Debug("开始处理单个数据包: {Data}", packetData);
            var parts = packetData.Split(','); // packetData is the logical string "guid,code,w,l,w,h,v,t"
            
            // ValidatePacket was already called by ExtractFirstValidPacket on these logical parts.
            // However, an extra check for count can be a safeguard.
            if (parts.Length != 8)
            {
                Log.Warning("ProcessPackageData received packet with unexpected part count ({Count}): {Data}", parts.Length, packetData);
                return;
            }

            var guid = parts[0].Trim();
            var code = parts[1].Trim();

            DateTime timestamp;
            if (long.TryParse(parts[7], out var unixTimestamp)) // Index 7 for timestamp
            {
                try
                {
                    var currentEpochSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    if (unixTimestamp > currentEpochSeconds + (3600 * 24 * 365 * 5)) // If timestamp is > 5 years in future (likely ms)
                    {
                         timestamp = DateTimeOffset.FromUnixTimeMilliseconds(unixTimestamp).UtcDateTime;
                    } else if (unixTimestamp > 100000000000) { // Very large number, likely milliseconds
                         timestamp = DateTimeOffset.FromUnixTimeMilliseconds(unixTimestamp).UtcDateTime;
                    }
                    else // Assume seconds
                    {
                        timestamp = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).UtcDateTime;
                    }
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    Log.Warning(ex, "时间戳 ({TimestampValue}) 超出有效范围，使用当前UTC时间", unixTimestamp);
                    timestamp = DateTime.UtcNow;
                }
            }
            else
            {
                timestamp = DateTime.UtcNow; // Fallback, though ValidatePacket should ensure it's a long
                Log.Warning("无法解析时间戳部分: {TimestampString} (来自包: {PacketData})，使用当前UTC时间", parts[7], packetData);
            }

            var isNoRead = string.Equals(code, "noread", StringComparison.OrdinalIgnoreCase);

            var package = PackageInfo.Create();
            package.CreateTime = timestamp; // 手动覆盖为解析出的UTC时间
            package.SetGuid(guid); // 设置 GUID
            package.SetBarcode(code); // Use 'code' (parts[1])

            double? length = null, width = null, height = null, volume = null;

            // Indices shifted: parts[2] to parts[6]
            if (float.TryParse(parts[2], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedWeight)) package.Weight = parsedWeight;
            if (double.TryParse(parts[3], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedLength)) length = parsedLength;
            if (double.TryParse(parts[4], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedWidth)) width = parsedWidth;
            if (double.TryParse(parts[5], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedHeight)) height = parsedHeight;
            if (double.TryParse(parts[6], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedVolume)) volume = parsedVolume;

            if (length.HasValue && width.HasValue && height.HasValue)
            {
                package.SetDimensions(length.Value, width.Value, height.Value);
                if (volume.HasValue) // Use the parsed volume if available
                {
                    package.Volume = volume.Value;
                }
                // else, volume will be auto-calculated by SetDimensions if that's its behavior
            }
            else
            {
                Log.Warning("包裹 {Barcode} (GUID: {Guid}) 尺寸信息不完整或无效 (L:'{LRaw}', W:'{WRaw}', H:'{HRaw}')",
                    package.Barcode, guid, parts[3], parts[4], parts[5]);
            }

            if (isNoRead)
            {
                package.SetStatus("无法识别条码");
                Log.Information("收到无法识别条码的包裹: GUID={Guid}, 时间={Time}, 重量={称重模块:F2}kg, 长={Length:F2}cm, 宽={Width:F2}cm, 高={Height:F2}cm, 体积={Volume:F3}m³ (按提供值)",
                    guid, timestamp, package.Weight, package.Length, package.Width, package.Height, package.Volume);
            }
            else
            {
                // No additional barcodes in the new fixed format
                Log.Information(
                    "收到包裹: GUID={Guid}, 条码={Barcode}, 时间={Time}, 重量={称重模块:F2}kg, 长={Length:F2}cm, 宽={Width:F2}cm, 高={Height:F2}cm, 体积={Volume:F3}m³ (按提供值)",
                    guid, package.Barcode, timestamp, package.Weight, package.Length, package.Width, package.Height, package.Volume);
            }

            _packageSubject.OnNext(package);

        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理单个包裹数据时发生错误: {Data}", packetData);
        }
    }
}