using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using Common.Models.Package;
using DeviceService.DataSourceDevices.Camera.Models;
using DeviceService.DataSourceDevices.Camera.Models.Camera;
using DeviceService.DataSourceDevices.TCP;
using Serilog;
using System.Windows.Media.Imaging;

namespace DeviceService.DataSourceDevices.Camera.TCP;

/// <summary>
///     TCP相机服务实现 (已重构为使用 TcpClientService)
/// </summary>
public class TcpCameraService
{
    private const int MinProcessInterval = 1000; // 最小处理间隔（毫秒）
    private const int MaxBufferSize = 1024 * 1024; // 最大缓冲区大小（1MB）

    private readonly Subject<PackageInfo> _packageSubject = new();
    private readonly object _processLock = new(); // 处理锁，确保同一时间只处理一个包裹
    private readonly Subject<BitmapSource> _realtimeImageSubject = new(); // 保留，但当前未使用

    private readonly TcpClientService _tcpClientService;
    private readonly string _host;
    private readonly int _port;
    private readonly StringBuilder _receiveBuffer = new(); // 用于累积接收数据的缓冲区

    private string _lastProcessedData = string.Empty; // 用于记录上一次处理的数据，避免重复处理
    private DateTime _lastProcessedTime = DateTime.MinValue; // 用于记录上一次处理数据的时间
    private bool _processingPackage; // 是否正在处理包裹

    public TcpCameraService(string host = "127.0.0.1", int port = 20011)
    {
        _host = host;
        _port = port;

        _tcpClientService = new TcpClientService(
            deviceName: $"TcpCamera-{host}-{port}",
            ipAddress: _host,
            port: _port,
            dataReceivedCallback: HandleDataReceived,
            connectionStatusCallback: HandleConnectionStatusChanged,
            autoReconnect: true // 启用自动重连
        );

        Log.Information("TCP相机服务已创建，将使用 TcpClientService 连接到 {Host}:{Port}", _host, _port);
    }

    public bool IsConnected { get; private set; }

    public IObservable<PackageInfo> PackageStream => _packageSubject.AsObservable();

    public IObservable<BitmapSource> ImageStream =>
        _realtimeImageSubject.AsObservable(); // 保留，但当前未使用

    // 实现 ImageStreamWithId，返回空流
    public IObservable<(BitmapSource Image, string CameraId)> ImageStreamWithId =>
        Observable.Empty<(BitmapSource Image, string CameraId)>();

    public event Action<string, bool>? ConnectionChanged;

    public void Dispose()
    {
        Log.Debug("正在 Dispose TCP相机服务 (TcpCameraService)...");
        _tcpClientService.Dispose(); // 释放 TcpClientService 实例
        _packageSubject.Dispose();
        _realtimeImageSubject.Dispose();
        Log.Debug("TCP相机服务 (TcpCameraService) Dispose 完成");
    }

    public IEnumerable<DeviceCameraInfo> GetCameraInfos()
    {
        return
        [
            new DeviceCameraInfo
            {
                SerialNumber = $"TCP_{_host}_{_port}", // 使用成员变量
                Model = "TCP 相机模块 (via TcpClientService)", // 更新模型名称
                IpAddress = _host,
                MacAddress = "N/A"
            }
        ];
    }

    public bool Start()
    {
        try
        {
            Log.Information("正在启动 TCP相机服务 (调用 TcpClientService.Connect)...");
            _tcpClientService.Connect(); // 尝试连接，Connect 方法不返回 bool，成功不抛异常
            // 无法直接判断是否立刻连接成功，依赖 HandleConnectionStatusChanged 回调更新状态
            // 返回 true 表示启动过程已开始
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动 TCP相机服务 (调用 TcpClientService.Connect) 失败");
            return false;
        }
    }

    public bool Stop()
    {
        try
        {
            Log.Information("正在停止 TCP相机服务 (调用 TcpClientService.Dispose)...");
            _tcpClientService.Dispose(); // Dispose 会处理断开连接和资源释放
            // 更新连接状态
            HandleConnectionStatusChanged(false);
            Log.Information("TCP相机服务 (TcpClientService) Dispose 调用完成");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止 TCP相机服务时发生错误");
            return false;
        }
    }

    private void HandleConnectionStatusChanged(bool isConnected)
    {
        Log.Debug("TcpCameraService - 连接状态变更: {IsConnected}", isConnected);
        IsConnected = isConnected;
        ConnectionChanged?.Invoke($"TCP_{_host}_{_port}", isConnected);
        if (!isConnected)
        {
            // 连接断开时清空接收缓冲区
            lock (_receiveBuffer)
            {
                _receiveBuffer.Clear();
            }
            Log.Debug("TCP 连接断开，接收缓冲区已清空");
        }
    }

    private void HandleDataReceived(byte[] data)
    {
        try
        {
            var receivedString = Encoding.UTF8.GetString(data);
            Log.Debug("TcpCameraService - 收到原始数据片段: {Data}", receivedString);

            lock (_receiveBuffer)
            {
                _receiveBuffer.Append(receivedString);

                // 检查缓冲区大小
                if (_receiveBuffer.Length > MaxBufferSize)
                {
                    Log.Warning("接收缓冲区大小超过限制 ({Size} > {MaxSize})，清空缓冲区", _receiveBuffer.Length, MaxBufferSize);
                    _receiveBuffer.Clear();
                    return;
                }

                // 处理缓冲区中的数据
                ProcessReceiveBuffer();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理接收到的TCP数据时发生错误");
            // 发生错误时考虑清空缓冲区，防止错误数据导致后续处理持续失败
            lock (_receiveBuffer)
            {
                _receiveBuffer.Clear();
            }
        }
    }

    private void ProcessReceiveBuffer()
    {
        var content = _receiveBuffer.ToString();
        var bufferModified = false;

        while (true)
        {
            var (packet, processedLength) = ExtractFirstValidPacket(content);

            if (packet != null)
            {
                Log.Debug("从缓冲区提取到数据包: {Packet}", packet);
                _receiveBuffer.Remove(0, processedLength);
                content = _receiveBuffer.ToString();
                bufferModified = true;

                var now = DateTime.Now;
                var timeSinceLastProcess = (now - _lastProcessedTime).TotalMilliseconds;

                if (packet != _lastProcessedData || timeSinceLastProcess > MinProcessInterval)
                {
                    Log.Debug("处理数据包: {Packet}", packet);
                    _lastProcessedData = packet;
                    _lastProcessedTime = now;

                    lock (_processLock)
                    {
                        if (!_processingPackage)
                        {
                            try
                            {
                                _processingPackage = true;
                                ProcessPackageData(packet);
                            }
                            finally
                            {
                                _processingPackage = false;
                            }
                        }
                        else
                        {
                            Log.Debug("正在处理其他包裹，跳过当前数据: {Data}", packet);
                        }
                    }
                }
                else
                {
                    Log.Debug("跳过重复的数据包(间隔{Interval}ms): {Packet}", timeSinceLastProcess, packet);
                }
            }
            else
            {
                Log.Debug("当前缓冲区未找到完整数据包，等待更多数据。当前缓冲区内容: {Content}", content);
                break;
            }
        }

        if (bufferModified)
        {
            Log.Debug("处理完成后，剩余缓冲区内容: {Content}", content);
        }
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
        string potentialPacketData = bufferContent.Substring(0, endOfPacketIndex);
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
                    // Heuristic to differentiate between seconds and milliseconds
                    var currentEpochSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    if (unixTimestamp > currentEpochSeconds + (3600 * 24 * 365 * 5)) // If timestamp is > 5 years in future (likely ms)
                    {
                         timestamp = DateTimeOffset.FromUnixTimeMilliseconds(unixTimestamp).LocalDateTime;
                    } else if (unixTimestamp > 100000000000) { // Very large number, likely milliseconds
                         timestamp = DateTimeOffset.FromUnixTimeMilliseconds(unixTimestamp).LocalDateTime;
                    }
                    else // Assume seconds
                    {
                        timestamp = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).LocalDateTime;
                    }
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    Log.Warning(ex, "时间戳 ({TimestampValue}) 超出有效范围，使用当前时间", unixTimestamp);
                    timestamp = DateTime.Now;
                }
            }
            else
            {
                timestamp = DateTime.Now; // Fallback, though ValidatePacket should ensure it's a long
                Log.Warning("无法解析时间戳部分: {TimestampString} (来自包: {PacketData})，使用当前时间", parts[7], packetData);
            }

            var isNoRead = string.Equals(code, "noread", StringComparison.OrdinalIgnoreCase);

            var package = PackageInfo.Create();
            package.SetGuid(guid); // 设置 GUID
            package.SetBarcode(code); // Use 'code' (parts[1])
            package.TriggerTimestamp = timestamp;

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
                package.SetStatus(PackageStatus.Failed, "无法识别条码");
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

    // 实现 GetAvailableCameras，返回占位符
    public IEnumerable<CameraBasicInfo> GetAvailableCameras()
    {
        // TCP service might not represent a specific camera model
        // Return a placeholder if connected
        if (IsConnected)
        {
            return new List<CameraBasicInfo>
            {
                new() { Id = $"TCP_{_host}_{_port}", Name = "TCP 数据源" }
            };
        }
        return [];
    }
}