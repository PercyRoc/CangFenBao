using System.Net.Sockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using Common.Models.Package;
using DeviceService.DataSourceDevices.Camera.Models.Camera;
using Serilog;
using System.Windows.Media.Imaging;

namespace DeviceService.DataSourceDevices.Camera.TCP;

/// <summary>
///     TCP相机服务实现
/// </summary>
internal class TcpCameraService(string host = "127.0.0.1", int port = 20011) : ICameraService
{
    private const int ReconnectInterval = 5000; // 重连间隔5秒
    private const int MinProcessInterval = 1000; // 最小处理间隔（毫秒）
    private const int MaxBufferSize = 1024 * 1024; // 最大缓冲区大小（1MB）
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Subject<PackageInfo> _packageSubject = new();
    private readonly object _processLock = new(); // 处理锁，确保同一时间只处理一个包裹

    private readonly Subject<BitmapSource> _realtimeImageSubject =
        new();

    private TcpClient? _client;
    private Task? _connectionTask;
    private string _lastProcessedData = string.Empty; // 用于记录上一次处理的数据，避免重复处理
    private DateTime _lastProcessedTime = DateTime.MinValue; // 用于记录上一次处理数据的时间
    private bool _processingPackage; // 是否正在处理包裹
    private NetworkStream? _stream;

    public bool IsConnected { get; private set; }

    public IObservable<PackageInfo> PackageStream => _packageSubject.AsObservable();

    public IObservable<BitmapSource> ImageStream =>
        _realtimeImageSubject.AsObservable();

    public event Action<string, bool>? ConnectionChanged;

    public void Dispose()
    {
        try
        {
            Log.Debug("正在 Dispose TCP相机服务..."); // 添加日志
            if (!_cancellationTokenSource.IsCancellationRequested)
            {
                _cancellationTokenSource.Cancel();
            }

            // 移除 Wait
            // try
            // {
            //     _connectionTask?.Wait(TimeSpan.FromSeconds(5));
            // } catch (...) { }

            // 关闭和 Dispose 移到 finally 块确保执行
            // _client?.Close(); // 不在此处 Close
            // _client?.Dispose(); // 移到 finally
            // _stream?.Dispose(); // 移到 finally
            _packageSubject.Dispose();
            _realtimeImageSubject.Dispose();
            // _cancellationTokenSource.Dispose(); // CancellationTokenSource 也应在 finally 中 dispose
            Log.Debug("TCP相机服务 Dispose - Subject 已 Dispose"); // 添加日志
        }
        catch (Exception ex)
        {
            Log.Error(ex, "释放TCP相机资源时发生错误（Dispose主体）"); // 区分错误来源
        }
        finally
        {
            // 确保资源总是被尝试释放
            try
            {
                _client?.Close();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Dispose 中关闭 Client 时出错");
            }

            try
            {
                _client?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Dispose 中 Dispose Client 时出错");
            }

            try
            {
                _stream?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Dispose 中 Dispose Stream 时出错");
            }

            try
            {
                _cancellationTokenSource.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Dispose 中 Dispose CancellationTokenSource 时出错");
            }

            // 短暂等待，允许后台任务完成一些清理
            try
            {
                _connectionTask?.Wait(TimeSpan.FromMilliseconds(100));
            }
            catch
            {
                // ignored
            } // 忽略异常

            Log.Debug("TCP相机 Dispose() 方法 finally 执行完毕"); // 添加日志
        }
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

    public bool Stop()
    {
        try
        {
            Log.Information("正在停止TCP相机服务..."); // 添加日志
            _cancellationTokenSource.Cancel();

            // 移除 Wait - 依赖后台任务自行响应取消
            // try
            // {
            //     _connectionTask?.Wait(TimeSpan.FromSeconds(5)); // 不再等待
            // }
            // catch (AggregateException ae)
            // {
            //     // 检查聚合异常是否仅包含取消相关的异常
            //     if (ae.InnerExceptions.All(e => e is OperationCanceledException || e is TaskCanceledException))
            //     {
            //         // 这是任务被取消时的预期行为
            //         Log.Debug("连接任务在停止时按预期取消。");
            //     }
            //     else
            //     {
            //         // 如果包含其他类型的异常，则记录警告
            //         Log.Warning(ae, "等待连接任务结束时发生非预期的聚合错误");
            //     }
            // }
            // catch (OperationCanceledException)
            // {
            //      // 如果 Wait 本身被取消（可能性较低）
            //      Log.Debug("等待连接任务的操作被取消。");
            // }
            // catch (Exception ex) // 捕获 Wait 期间发生的任何其他意外异常
            // {
            //     Log.Warning(ex, "等待连接任务结束时发生其他未知错误");
            // }


            // 直接尝试关闭资源
            // 注意：如果在 ReceiveDataAsync 仍在运行时调用 Close/Dispose，可能会导致该方法内部抛出 ObjectDisposedException。
            // ReadAsync 应该首先通过 CancellationToken 停止。
            _client?.Close(); // 关闭连接可能会导致 ReadAsync 抛出异常
            // _client?.Dispose(); // Dispose 通常在 Close 后安全
            // _stream?.Dispose(); // Dispose Stream 同理

            IsConnected = false;
            ConnectionChanged?.Invoke($"TCP_{host}_{port}", false);

            Log.Information("TCP相机服务停止信号已发送，正在关闭资源..."); // 修改日志
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止TCP相机时发生错误");
            return false;
        }
        finally
        {
            // Dispose 应该在 finally 中进行，确保总是执行
            _client?.Dispose();
            _stream?.Dispose();
            // 可以在这里尝试等待一小段时间，但不阻塞关键路径
            try
            {
                _connectionTask?.Wait(TimeSpan.FromMilliseconds(100)); // 短暂等待，非阻塞
            }
            catch
            {
                // ignored
            } // 忽略异常

            Log.Information("TCP相机 Stop() 方法执行完毕"); // 添加日志
        }
    }

    private async Task MaintenanceConnectionAsync()
    {
        while (!_cancellationTokenSource.Token.IsCancellationRequested)
        {
            try
            {
                if (!IsConnected) await ConnectAsync();

                if (IsConnected) await ReceiveDataAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "连接或接收数据时发生错误");
            }

            if (_cancellationTokenSource.Token.IsCancellationRequested) continue;

            Log.Information("等待 {Interval} 毫秒后尝试重连...", ReconnectInterval);
            // 使用 Try/Catch 避免 Task.Delay 在取消时抛出异常导致循环意外终止
            try
            {
                await Task.Delay(ReconnectInterval, _cancellationTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                Log.Debug("MaintenanceConnectionAsync 的 Task.Delay 被取消。");
                break; // 退出循环
            }
        }

        Log.Information("MaintenanceConnectionAsync 循环结束。"); // 添加日志
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
            if (_stream != null)
            {
                await _stream.DisposeAsync();
            }

            _stream = null;
            throw;
        }
    }

    private async Task ReceiveDataAsync()
    {
        var buffer = new byte[4096];
        var data = new StringBuilder();

        try
        {
            Log.Debug("ReceiveDataAsync 任务开始。"); // 添加日志
            while (!_cancellationTokenSource.Token.IsCancellationRequested && _stream != null)
            {
                var bytesRead = await _stream.ReadAsync(buffer, _cancellationTokenSource.Token);
                if (bytesRead == 0)
                {
                    Log.Debug("TCP连接被远程主机关闭");
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
        catch (OperationCanceledException) // 特别捕获取消异常
        {
            Log.Debug("ReceiveDataAsync 任务被取消");
        }
        catch (ObjectDisposedException ode) // 捕获因 Stop/Dispose 中关闭 Stream 导致的异常
        {
            Log.Debug(ode, "ReceiveDataAsync 尝试在已释放的对象上操作 (可能是 Stream 或 Client)");
        }
        catch (IOException ioex) // 捕获其他 IO 错误，例如连接被强制关闭
        {
            Log.Warning(ioex, "ReceiveDataAsync 中发生 IO 错误");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "接收TCP数据时发生未知错误"); // 保留未知错误日志
        }
        finally
        {
            IsConnected = false;
            ConnectionChanged?.Invoke($"TCP_{host}_{port}", false);
            Log.Information("TCP接收任务(ReceiveDataAsync)结束"); // 修改日志
        }
    }

    /// <summary>
    ///     处理接收到的数据
    /// </summary>
    private void ProcessReceivedData(StringBuilder data)
    {
        var content = data.ToString();

        // 检查缓冲区大小
        if (data.Length > MaxBufferSize)
        {
            Log.Warning("缓冲区大小超过限制 ({Size} > {MaxSize})，清空缓冲区", data.Length, MaxBufferSize);
            data.Clear();
            return;
        }

        // 检查数据是否包含逗号，如果不包含，可能不是有效的数据包
        if (!content.Contains(','))
        {
            Log.Debug("数据包不包含逗号，丢弃: {Data}", content);
            data.Clear();
            return;
        }

        // 提取所有可能的数据包
        var packets = ExtractDataPackets(content);

        // 清空缓冲区，因为不符合规则的数据包直接丢弃
        data.Clear();

        // 处理所有完整的数据包
        foreach (var packet in packets)
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
    }

    /// <summary>
    ///     从数据流中提取完整的数据包
    /// </summary>
    private static List<string> ExtractDataPackets(string data)
    {
        var result = new List<string>();

        // 查找所有符合格式的数据包
        // 格式: 条码1,条码2,...,条码N,重量,长度,宽度,高度,体积,时间戳
        var parts = data.Split(',');

        // 检查是否有足够的基本字段（至少7个字段：1个条码 + 重量 + 长度 + 宽度 + 高度 + 体积 + 时间戳）
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

                // 检查是否达到完整数据包（至少7个字段）
                if (fieldCount < 7) continue;
                // 验证数据包格式
                if (ValidatePacket(currentPacket))
                {
                    completePackets.Add(string.Join(",", currentPacket));
                }
                else
                {
                    Log.Warning("无效的数据包格式，丢弃: {Packet}", string.Join(",", currentPacket));
                }

                currentPacket.Clear();
                fieldCount = 0;
            }

            // 添加所有完整的数据包
            result.AddRange(completePackets);
        }
        else
        {
            Log.Debug("数据包字段数量不足，丢弃: {Data}", data);
        }

        return result;
    }

    /// <summary>
    ///     验证数据包格式
    /// </summary>
    private static bool ValidatePacket(List<string> packet)
    {
        if (packet.Count < 7) return false;

        // 验证时间戳
        if (!long.TryParse(packet[^1], out _)) return false;

        // 验证数值字段
        if (!float.TryParse(packet[^6], out _)) return false; // 重量
        if (!double.TryParse(packet[^5], out _)) return false; // 长度
        if (!double.TryParse(packet[^4], out _)) return false; // 宽度
        if (!double.TryParse(packet[^3], out _)) return false; // 高度
        if (!double.TryParse(packet[^2], out _)) return false; // 体积

        // 验证条码
        var barcode = packet[0].Trim();
        if (string.IsNullOrEmpty(barcode) && !string.Equals(barcode, "noread", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private void ProcessPackageData(string data)
    {
        try
        {
            Log.Debug("开始处理数据: {Data}", data);
            var parts = data.Split(',');
            if (parts.Length < 7)
            {
                Log.Warning("无效的数据格式: {Data}", data);
                return;
            }

            // 解析时间戳
            DateTime timestamp;
            if (long.TryParse(parts[^1], out var unixTimestamp))
            {
                try
                {
                    // 判断时间戳是秒还是毫秒
                    // 如果时间戳大于当前时间戳，说明是毫秒
                    var currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (unixTimestamp > currentTimestamp)
                    {
                        // 毫秒时间戳
                        timestamp = DateTimeOffset.FromUnixTimeMilliseconds(unixTimestamp).LocalDateTime;
                    }
                    else if (unixTimestamp > 1000000000000) // 如果时间戳大于这个值，说明是毫秒
                    {
                        // 毫秒时间戳
                        timestamp = DateTimeOffset.FromUnixTimeMilliseconds(unixTimestamp).LocalDateTime;
                    }
                    else
                    {
                        // 秒时间戳
                        timestamp = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).LocalDateTime;
                    }
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

            // 只使用第一个条码
            var firstBarcode = parts[0].Trim();
            if (string.IsNullOrEmpty(firstBarcode))
            {
                Log.Warning("第一个条码为空，跳过处理");
                return;
            }

            // 1. 创建包裹，使用静态工厂方法
            var package = PackageInfo.Create();

            // 2. 设置条码 (段码未知，设为 null)
            package.SetBarcode(firstBarcode);

            // 3. 设置触发时间戳 (保持)
            package.SetTriggerTimestamp(timestamp);

            // 解析并设置重量、尺寸
            double? length = null, width = null, height = null;

            // 解析重量
            if (float.TryParse(parts[^6], out var parsedWeight))
            {
                package.SetWeight(parsedWeight); // 4. 设置重量
            }

            // 解析长度
            if (double.TryParse(parts[^5], out var parsedLength))
            {
                length = parsedLength;
            }

            // 解析宽度
            if (double.TryParse(parts[^4], out var parsedWidth))
            {
                width = parsedWidth;
            }

            // 解析高度
            if (double.TryParse(parts[^3], out var parsedHeight))
            {
                height = parsedHeight;
            }

            // 如果长宽高都有效，则设置尺寸
            if (length.HasValue && width.HasValue && height.HasValue)
            {
                package.SetDimensions(length.Value, width.Value, height.Value); // 5. 设置尺寸 (会自动计算体积)
            }
            else
            {
                Log.Warning("包裹 {Barcode} 尺寸信息不完整或无效 (L:{L}, W:{W}, H:{H})",
                    package.Barcode, parts[^5], parts[^4], parts[^3]);
            }

            _packageSubject.OnNext(package);

            // 如果存在多个条码，记录日志
            if (parts.Length > 7)
            {
                var additionalBarcodes = string.Join(",", parts[1..^6].Select(b => b.Trim()));
                Log.Information(
                    "收到包裹: 条码={Barcode}, 时间={Time}, 重量={Weight:F2}kg, 长={Length:F2}cm, 宽={Width:F2}cm, 高={Height:F2}cm, 额外条码={AdditionalBarcodes}",
                    package.Barcode, timestamp, package.Weight, package.Length, package.Width, package.Height,
                    additionalBarcodes);
            }
            else
            {
                Log.Information(
                    "收到包裹: 条码={Barcode}, 时间={Time}, 重量={Weight:F2}kg, 长={Length:F2}cm, 宽={Width:F2}cm, 高={Height:F2}cm",
                    package.Barcode, timestamp, package.Weight, package.Length, package.Width, package.Height);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹数据时发生错误: {Data}", data);
        }
    }
}