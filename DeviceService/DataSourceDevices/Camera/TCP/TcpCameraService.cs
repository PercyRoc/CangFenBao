using System.Net.Sockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using Common.Models.Package;
using DeviceService.DataSourceDevices.Camera.Models;
using DeviceService.DataSourceDevices.Camera.Models.Camera;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DeviceService.DataSourceDevices.Camera.TCP;

/// <summary>
/// TCP相机服务实现
/// </summary>
public class TcpCameraService(string host = "127.0.0.1", int port = 2000) : ICameraService
{
    private readonly Subject<PackageInfo> _packageSubject = new();
    private readonly Subject<(Image<Rgba32> image, IReadOnlyList<BarcodeLocation> barcodes)> _realtimeImageSubject = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private Task? _connectionTask;
    private const int ReconnectInterval = 5000; // 重连间隔5秒
    private int _lastPackageIndex = 0; // 用于记录上一次分配的包裹序号，改为非静态
    private string _lastProcessedData = string.Empty; // 用于记录上一次处理的数据，避免重复处理
    private DateTime _lastProcessedTime = DateTime.MinValue; // 用于记录上一次处理数据的时间
    private const int MinProcessInterval = 1000; // 最小处理间隔（毫秒）
    private bool _processingPackage = false; // 是否正在处理包裹
    private readonly object _processLock = new(); // 处理锁，确保同一时间只处理一个包裹

    public bool IsConnected { get; private set; }

    public IObservable<PackageInfo> PackageStream => _packageSubject.AsObservable();

    public IObservable<(Image<Rgba32> image, IReadOnlyList<BarcodeLocation> barcodes)> ImageStream => 
        _realtimeImageSubject.AsObservable();

    public event Action<string, bool>? ConnectionChanged;

    public void Dispose()
    {
        try
        {
            _cancellationTokenSource.Cancel();
            try
            {
                _connectionTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "等待连接任务结束时发生错误");
            }
            
            _client?.Close();
            _client?.Dispose();
            _stream?.Dispose();
            _packageSubject.Dispose();
            _realtimeImageSubject.Dispose();
            _cancellationTokenSource.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "释放TCP相机资源时发生错误");
        }
    }

    public bool ExecuteSoftTrigger()
    {
        // TCP相机不支持软触发
        return false;
    }

    public IEnumerable<DeviceCameraInfo> GetCameraInfos()
    {
        return
        [
            new DeviceCameraInfo
            {
                SerialNumber = $"TCP_{host}_{port}",
                Model = "TCP Camera",
                IpAddress = host,
                MacAddress = "N/A"
            }
        ];
    }

    public bool Start()
    {
        try
        {
            if (IsConnected)
            {
                Log.Information("TCP相机已经连接");
                return true;
            }

            // 启动连接任务
            _connectionTask = Task.Run(MaintenanceConnectionAsync, _cancellationTokenSource.Token);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动TCP相机失败");
            return false;
        }
    }

    private async Task MaintenanceConnectionAsync()
    {
        while (!_cancellationTokenSource.Token.IsCancellationRequested)
        {
            try
            {
                if (!IsConnected)
                {
                    await ConnectAsync();
                }

                if (IsConnected)
                {
                    await ReceiveDataAsync();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "连接或接收数据时发生错误");
            }

            if (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                Log.Information("等待 {Interval} 毫秒后尝试重连...", ReconnectInterval);
                await Task.Delay(ReconnectInterval, _cancellationTokenSource.Token);
            }
        }
    }

    private async Task ConnectAsync()
    {
        try
        {
            Log.Information("正在连接TCP相机服务器 {Host}:{Port}...", host, port);
            
            _client?.Dispose();
            _client = new TcpClient();
            await _client.ConnectAsync(host, port, _cancellationTokenSource.Token);
            _stream = _client.GetStream();
            IsConnected = true;
            
            ConnectionChanged?.Invoke($"TCP_{host}_{port}", true);
            Log.Information("TCP相机连接成功");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "连接TCP相机失败");
            IsConnected = false;
            _client?.Dispose();
            _client = null;
            _stream?.Dispose();
            _stream = null;
            throw;
        }
    }

    public bool Stop()
    {
        try
        {
            _cancellationTokenSource.Cancel();
            try
            {
                _connectionTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "等待连接任务结束时发生错误");
            }
            
            _client?.Close();
            _client?.Dispose();
            _stream?.Dispose();
            
            IsConnected = false;
            ConnectionChanged?.Invoke($"TCP_{host}_{port}", false);
            
            Log.Information("TCP相机已停止");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止TCP相机时发生错误");
            return false;
        }
    }

    public void UpdateConfiguration(CameraSettings config)
    {
    }

    private async Task ReceiveDataAsync()
    {
        var buffer = new byte[4096];
        var data = new StringBuilder();

        try
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested && _stream != null)
            {
                var bytesRead = await _stream.ReadAsync(buffer, _cancellationTokenSource.Token);
                if (bytesRead == 0)
                {
                    Log.Debug("连接已关闭");
                    break;
                }

                var receivedData = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                Log.Debug("收到原始数据: {Data}", receivedData);
                
                // 将接收到的数据添加到缓冲区
                data.Append(receivedData);
                
                // 处理数据
                ProcessReceivedData(data);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Debug("接收任务被取消");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "接收TCP数据时发生错误");
        }
        finally
        {
            IsConnected = false;
            ConnectionChanged?.Invoke($"TCP_{host}_{port}", false);
            Log.Information("TCP接收任务结束");
        }
    }
    
    /// <summary>
    /// 处理接收到的数据
    /// </summary>
    private void ProcessReceivedData(StringBuilder data)
    {
        var content = data.ToString();
        
        // 检查数据是否包含逗号，如果不包含，可能不是有效的数据包
        if (!content.Contains(','))
        {
            return;
        }
        
        // 提取所有可能的数据包
        var dataPackets = ExtractDataPackets(content);
        
        // 清除缓冲区
        data.Clear();
        
        // 如果有未处理完的数据，保留在缓冲区中
        if (dataPackets.RemainingData.Length > 0)
        {
            data.Append(dataPackets.RemainingData);
        }
        
        // 处理所有完整的数据包
        foreach (var packet in dataPackets.Packets)
        {
            // 检查是否与上一次处理的数据相同，并且时间间隔是否足够
            var now = DateTime.Now;
            var timeSinceLastProcess = (now - _lastProcessedTime).TotalMilliseconds;
            
            if (packet != _lastProcessedData || timeSinceLastProcess > MinProcessInterval)
            {
                Log.Debug("处理数据包: {Packet}", packet);
                _lastProcessedData = packet; // 记录本次处理的数据
                _lastProcessedTime = now; // 记录处理时间
                
                // 使用锁确保同一时间只处理一个包裹
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
    }
    
    /// <summary>
    /// 从数据流中提取完整的数据包
    /// </summary>
    private (List<string> Packets, string RemainingData) ExtractDataPackets(string data)
    {
        var result = new List<string>();
        var remainingData = string.Empty;
        
        // 查找所有符合格式的数据包
        // 格式: 条码,重量,长度,宽度,高度,体积,时间戳
        // 例如: UNAM26278846CN,0,0,0,0,0,1741564603
        
        // 简单的方法：检查是否包含7个字段
        var parts = data.Split(',');
        
        if (parts.Length >= 7)
        {
            // 可能有一个或多个完整的数据包
            var completePackets = new List<string>();
            var currentPacket = new List<string>();
            var fieldCount = 0;
            
            foreach (var part in parts)
            {
                currentPacket.Add(part);
                fieldCount++;
                
                if (fieldCount == 7)
                {
                    // 找到一个完整的数据包
                    completePackets.Add(string.Join(",", currentPacket));
                    currentPacket.Clear();
                    fieldCount = 0;
                }
            }
            
            // 添加所有完整的数据包
            result.AddRange(completePackets);
            
            // 如果有剩余的字段，保存为未处理的数据
            if (fieldCount > 0)
            {
                remainingData = string.Join(",", currentPacket);
            }
        }
        else
        {
            // 数据不完整，全部保存为未处理的数据
            remainingData = data;
        }
        
        return (result, remainingData);
    }

    private void ProcessPackageData(string data)
    {
        try
        {
            Log.Debug("开始处理数据: {Data}", data);
            var parts = data.Split(',');
            if (parts.Length != 7)
            {
                Log.Warning("无效的数据格式: {Data}", data);
                return;
            }

            // 解析时间戳
            DateTime timestamp;
            if (long.TryParse(parts[6], out var unixTimestamp))
            {
                timestamp = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).LocalDateTime;
            }
            else
            {
                timestamp = DateTime.Now;
                Log.Warning("无法解析时间戳: {Timestamp}，使用当前时间", parts[6]);
            }

            _lastPackageIndex++; // 使用自增操作符
            var package = new PackageInfo
            {
                Index = _lastPackageIndex,
                Barcode = parts[0].Trim(),
                CreateTime = timestamp
            };

            // 解析重量
            if (float.TryParse(parts[1], out var weight))
            {
                package.Weight = weight;
            }

            // 解析长度
            if (double.TryParse(parts[2], out var length))
            {
                package.Length = length;
            }

            // 解析宽度
            if (double.TryParse(parts[3], out var width))
            {
                package.Width = width;
            }

            // 解析高度
            if (double.TryParse(parts[4], out var height))
            {
                package.Height = height;
            }

            // 解析体积
            if (double.TryParse(parts[5], out var volume))
            {
                package.Volume = volume;
            }

            package.SetTriggerTimestamp(timestamp);
            _packageSubject.OnNext(package);

            Log.Information("收到包裹: 序号={Index}, 条码={Barcode}, 时间={Time}, 重量={Weight:F2}kg, 长={Length:F2}cm, 宽={Width:F2}cm, 高={Height:F2}cm, 体积={Volume:F2}cm³", 
                package.Index, package.Barcode, timestamp, package.Weight, package.Length, package.Width, package.Height, package.Volume);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹数据时发生错误: {Data}", data);
        }
    }
}