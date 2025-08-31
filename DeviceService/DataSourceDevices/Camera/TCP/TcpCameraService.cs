using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Windows.Media.Imaging;
using Common.Models.Package;
using DeviceService.DataSourceDevices.Camera.Models;
using DeviceService.DataSourceDevices.Camera.Models.Camera;
using DeviceService.DataSourceDevices.TCP;
using Serilog;

namespace DeviceService.DataSourceDevices.Camera.TCP;

/// <summary>
///     TCP相机服务实现 (已重构为使用 TcpClientService)
/// </summary>
internal class TcpCameraService : ICameraService
{
    private const int MinProcessInterval = 1000; // 最小处理间隔（毫秒）
    private const int MaxBufferSize = 1024 * 1024; // 最大缓冲区大小（1MB）
    private readonly string _host;

    private readonly Subject<PackageInfo> _packageSubject = new();
    private readonly int _port;
    private readonly object _processLock = new(); // 处理锁，确保同一时间只处理一个包裹
    private readonly Subject<BitmapSource> _realtimeImageSubject = new(); // 保留，但当前未使用
    private readonly StringBuilder _receiveBuffer = new(); // 用于累积接收数据的缓冲区

    private readonly TcpClientService _tcpClientService;

    private string _lastProcessedData = string.Empty; // 用于记录上一次处理的数据，避免重复处理
    private DateTime _lastProcessedTime = DateTime.MinValue; // 用于记录上一次处理数据的时间
    private bool _processingPackage; // 是否正在处理包裹

    public TcpCameraService(string host = "127.0.0.1", int port = 20011)
    {
        _host = host;
        _port = port;

        _tcpClientService = new TcpClientService(
            $"TcpCamera-{host}-{port}",
            _host,
            _port,
            HandleDataReceived,
            HandleConnectionStatusChanged // 启用自动重连
        );

        Log.Information("TCP相机服务已创建，将使用 TcpClientService 连接到 {Host}:{Port}", _host, _port);
    }

    public bool IsConnected { get; private set; }

    public IObservable<PackageInfo> PackageStream => _packageSubject.AsObservable();

    public IObservable<BitmapSource> ImageStream => _realtimeImageSubject.AsObservable(); // 保留，但当前未使用

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

    // 实现 GetAvailableCameras，返回占位符
    public IEnumerable<CameraBasicInfo> GetAvailableCameras()
    {
        // TCP service might not represent a specific camera model
        // Return a placeholder if connected
        if (IsConnected)
            return new List<CameraBasicInfo>
            {
                new()
                {
                    Id = $"TCP_{_host}_{_port}",
                    Name = "TCP 数据源"
                }
            };
        return [];
    }

    public IEnumerable<DeviceCameraInfo> GetCameraInfos()
    {
        return
        [
            new DeviceCameraInfo
            {
                SerialNumber = $"TCP_{_host}_{_port}", // 使用成员变量
                Model = "TCP Camera (via TcpClientService)", // 更新模型名称
                IpAddress = _host,
                MacAddress = "N/A"
            }
        ];
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
                            try
                            {
                                _processingPackage = true;
                                ProcessPackageData(packet);
                            }
                            finally
                            {
                                _processingPackage = false;
                            }
                        else
                            Log.Debug("正在处理其他包裹，跳过当前数据: {Data}", packet);
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

        if (bufferModified) Log.Debug("处理完成后，剩余缓冲区内容: {Content}", content);
    }

    private static (string? Packet, int ProcessedLength) ExtractFirstValidPacket(string bufferContent)
    {
        var potentialPackets = bufferContent.Split([','], StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < potentialPackets.Length; i++)
        {
            if (potentialPackets.Length - i < 7) continue;
            for (var j = i + 6; j < potentialPackets.Length; j++)
            {
                var subPacketParts = potentialPackets.Skip(i).Take(j - i + 1).ToList();
                if (!ValidatePacket(subPacketParts)) continue;
                var packetString = string.Join(",", subPacketParts);
                var endIndex = bufferContent.IndexOf(packetString, StringComparison.Ordinal);
                if (endIndex == -1) continue;
                var processedLength = endIndex + packetString.Length;
                if (processedLength != bufferContent.Length &&
                    (processedLength >= bufferContent.Length || bufferContent[processedLength] != ',')) continue;
                Log.Debug("找到有效包: {Packet}, 原始字符串长度近似: {Length}", packetString, packetString.Length);
                var approxLength = CalculateApproximateLength(bufferContent, subPacketParts);
                return (packetString, approxLength);
            }
        }

        return (null, 0);
    }

    private static int CalculateApproximateLength(string originalBuffer, List<string> parts)
    {
        if (parts.Count == 0) return 0;
        var joinedString = string.Join(",", parts);
        var index = originalBuffer.IndexOf(joinedString, StringComparison.Ordinal);
        if (index != -1) return index + joinedString.Length;
        return joinedString.Length + (parts.Count > 0 ? parts.Count - 1 : 0);
    }

    private static bool ValidatePacket(List<string> packet)
    {
        if (packet.Count < 7) return false;

        if (!long.TryParse(packet[^1], out _)) return false;

        if (!float.TryParse(packet[^6], out _)) return false;
        if (!double.TryParse(packet[^5], out _)) return false;
        if (!double.TryParse(packet[^4], out _)) return false;
        if (!double.TryParse(packet[^3], out _)) return false;
        if (!double.TryParse(packet[^2], out _)) return false;

        var firstBarcode = packet[0].Trim();
        return !string.IsNullOrEmpty(firstBarcode) ||
               string.Equals(firstBarcode, "noread", StringComparison.OrdinalIgnoreCase);
    }

    private void ProcessPackageData(string packetData)
    {
        try
        {
            Log.Debug("开始处理单个数据包: {Data}", packetData);
            var parts = packetData.Split(',');
            if (!ValidatePacket([.. parts]))
            {
                Log.Warning("无效的数据包格式 (再次验证失败): {Data}", packetData);
                return;
            }

            DateTime timestamp;
            if (long.TryParse(parts[^1], out var unixTimestamp))
            {
                try
                {
                    var currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (unixTimestamp > currentTimestamp || unixTimestamp > 1000000000000)
                        timestamp = DateTimeOffset.FromUnixTimeMilliseconds(unixTimestamp).LocalDateTime;
                    else
                        timestamp = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).LocalDateTime;
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    Log.Warning(ex, "时间戳超出有效范围: {Timestamp}，使用当前时间", unixTimestamp);
                    timestamp = DateTime.Now;
                }
            }
            else
            {
                timestamp = DateTime.Now;
                Log.Warning("无法解析时间戳: {Timestamp}，使用当前时间", parts[^1]);
            }

            var firstBarcode = parts[0].Trim();
            var isNoRead = string.Equals(firstBarcode, "noread", StringComparison.OrdinalIgnoreCase);

            var package = PackageInfo.Create();
            package.SetBarcode(firstBarcode);

            double? length = null, width = null, height = null, volume = null;

            if (float.TryParse(parts[^6], out var parsedWeight)) package.SetWeight(parsedWeight);
            if (double.TryParse(parts[^5], out var parsedLength)) length = parsedLength;
            if (double.TryParse(parts[^4], out var parsedWidth)) width = parsedWidth;
            if (double.TryParse(parts[^3], out var parsedHeight)) height = parsedHeight;
            if (double.TryParse(parts[^2], out var parsedVolume)) volume = parsedVolume;

            if (length.HasValue && width.HasValue && height.HasValue)
            {
                package.SetDimensions(length.Value, width.Value, height.Value);
                if (volume.HasValue) package.SetVolume(volume.Value);
            }
            else
            {
                Log.Warning("包裹 {Barcode} 尺寸信息不完整或无效 (L:{L}, W:{W}, H:{H})",
                    package.Barcode, parts[^5], parts[^4], parts[^3]);
            }

            if (isNoRead)
            {
                package.SetStatus(PackageStatus.Failed, "无法识别条码");
                Log.Information(
                    "收到无法识别条码的包裹: 时间={Time}, 重量={Weight:F2}kg, 长={Length:F2}cm, 宽={Width:F2}cm, 高={Height:F2}cm, 体积={Volume:F3}m³",
                    timestamp, package.Weight, package.Length, package.Width, package.Height, package.Volume);
            }
            else
            {
                if (parts.Length > 7)
                {
                    var additionalBarcodes = string.Join(",", parts[1..^6].Select(b => b.Trim()));
                    Log.Information(
                        "收到包裹: 条码={Barcode}, 时间={Time}, 重量={Weight:F2}kg, 长={Length:F2}cm, 宽={Width:F2}cm, 高={Height:F2}cm, 体积={Volume:F3}m³, 额外条码={AdditionalBarcodes}",
                        package.Barcode, timestamp, package.Weight, package.Length, package.Width, package.Height,
                        package.Volume,
                        additionalBarcodes);
                }
                else
                {
                    Log.Information(
                        "收到包裹: 条码={Barcode}, 时间={Time}, 重量={Weight:F2}kg, 长={Length:F2}cm, 宽={Width:F2}cm, 高={Height:F2}cm, 体积={Volume:F3}m³",
                        package.Barcode, timestamp, package.Weight, package.Length, package.Width, package.Height,
                        package.Volume);
                }
            }

            _packageSubject.OnNext(package);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理单个包裹数据时发生错误: {Data}", packetData);
        }
    }
}