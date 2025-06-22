using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace TcpCameraSimulator;

/// <summary>
/// 协调的相机模拟器 - 作为TCP服务器等待客户端连接，根据PLC信号延迟发送相机数据
/// </summary>
public class CoordinatedCameraSimulator : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly Random _random = new();
    private volatile bool _isRunning = true;
    
    // 延迟配置
    private readonly int _minDelayMs;
    private readonly int _maxDelayMs;
    
    // 统计信息
    private volatile int _totalReceived = 0;
    private volatile int _totalSent = 0;
    private volatile int _totalSuccessful = 0;
    private volatile int _totalFailed = 0;
    private volatile int _totalTimeouts = 0;
    
    // PLC信号处理队列
    private readonly ConcurrentQueue<PlcSignal> _pendingSignals = new();
    private readonly SemaphoreSlim _signalSemaphore = new(0);

    // 【新增】TCP服务器和客户端管理
    private TcpListener? _listener;
    private readonly List<TcpClient> _connectedClients = new();
    private readonly object _clientsLock = new();
    
    // 延迟统计
    private readonly ConcurrentQueue<double> _delayMeasurements = new();
    
    // 相机模拟相关
    private static TcpListener? _cameraListener;
    private static readonly List<TcpClient> CameraClients = new();
    private static readonly Channel<string> CameraDataChannel = Channel.CreateUnbounded<string>();
    private static CancellationTokenSource? _cts;
    
    // 【新增】PLC模拟相关
    private static TcpClient? _plcClient;
    private static NetworkStream? _plcStream;
    private static readonly ConcurrentDictionary<ushort, DateTimeOffset> SentPlcSignals = new();
    private static ushort _currentPlcPackageNumber = 1;

    public CoordinatedCameraSimulator(string host = "127.0.0.1", int port = 20011, 
        int minDelayMs = 800, int maxDelayMs = 900)
    {
        _host = host;
        _port = port;
        _minDelayMs = minDelayMs;
        _maxDelayMs = maxDelayMs;
    }
    
    public int TotalReceived => _totalReceived;
    public int TotalSent => _totalSent;
    public int TotalSuccessful => _totalSuccessful;
    public int TotalFailed => _totalFailed;
    public int TotalTimeouts => _totalTimeouts;
    
    /// <summary>
    /// 检查是否有客户端连接
    /// </summary>
    public bool HasConnectedClients
    {
        get
        {
            lock (_clientsLock)
            {
                return _connectedClients.Count > 0 && _connectedClients.Any(c => c.Connected);
            }
        }
    }

    /// <summary>
    /// 接收PLC信号
    /// </summary>
    public void OnPlcSignalReceived(PlcSignal signal)
    {
        Interlocked.Increment(ref _totalReceived);
        _pendingSignals.Enqueue(signal);
        _signalSemaphore.Release();
        
        Log.Information("📸 [相机模拟器] 收到PLC信号: 序号={PackageNumber}, 条码={Barcode}, 将延迟{DelayRange}ms发送", 
            signal.PackageNumber, signal.Barcode, $"{_minDelayMs}-{_maxDelayMs}");
    }
    
    /// <summary>
    /// 启动相机模拟器（服务器模式）
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Log.Information("📸 [相机模拟器] 启动服务器模式，监听地址: {Host}:{Port}, 延迟范围: {MinDelay}-{MaxDelay}ms", 
            _host, _port, _minDelayMs, _maxDelayMs);
        
        try
        {
            // 启动TCP服务器
            if (!IPAddress.TryParse(_host, out var ipAddress))
            {
                Log.Error("无效的IP地址: {Host}，将使用 IPAddress.Any", _host);
                ipAddress = IPAddress.Any;
            }
            
            _listener = new TcpListener(ipAddress, _port);
            _listener.Start();
            
            Log.Information("📸 [相机模拟器] TCP服务器已启动，正在监听连接...");
            
            // 启动信号处理器
            _ = ProcessSignalsAsync(cancellationToken);
            
            // 启动客户端接受循环
            await AcceptClientsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "📸 [相机模拟器] 启动失败");
            throw;
        }
        finally
        {
            _listener?.Stop();
            Log.Information("📸 [相机模拟器] 服务器已停止");
        }
    }

    /// <summary>
    /// 接受客户端连接
    /// </summary>
    private async Task AcceptClientsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (_isRunning && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener!.AcceptTcpClientAsync();
                    var clientEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "未知客户端";
                    
                    Log.Information("📸 [相机模拟器] 接受客户端连接: {ClientEndPoint}", clientEndPoint);
                    
                    // 配置TCP连接
                    client.NoDelay = true;
                    client.ReceiveBufferSize = 8192;
                    client.SendBufferSize = 8192;
                    
                    lock (_clientsLock)
                    {
                        _connectedClients.Add(client);
                    }
                    
                    // 为每个客户端启动处理任务
                    _ = HandleClientAsync(client, cancellationToken);
                }
                catch (ObjectDisposedException)
                {
                    Log.Debug("📸 [相机模拟器] TCP监听器已被释放");
                    break;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    Log.Error(ex, "📸 [相机模拟器] 接受客户端连接时发生错误");
                    await Task.Delay(1000, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log.Debug("📸 [相机模拟器] 客户端接受循环被取消");
        }
    }

    /// <summary>
    /// 处理单个客户端连接
    /// </summary>
    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var clientEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "未知客户端";
        
        try
        {
            Log.Information("📸 [相机模拟器] 开始处理客户端: {ClientEndPoint}", clientEndPoint);
            
            // 保持连接直到取消或客户端断开
            while (_isRunning && !cancellationToken.IsCancellationRequested && client.Connected)
            {
                await Task.Delay(1000, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Debug("📸 [相机模拟器] 客户端处理任务被取消: {ClientEndPoint}", clientEndPoint);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "📸 [相机模拟器] 处理客户端时发生错误: {ClientEndPoint}", clientEndPoint);
        }
        finally
        {
            lock (_clientsLock)
            {
                _connectedClients.Remove(client);
            }
            
            try
            {
                client.Close();
            }
            catch { }
            
            Log.Information("📸 [相机模拟器] 客户端连接已关闭: {ClientEndPoint}", clientEndPoint);
        }
    }
    
    /// <summary>
    /// 处理PLC信号队列
    /// </summary>
    private async Task ProcessSignalsAsync(CancellationToken cancellationToken)
    {
        while (_isRunning && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                // 等待新的PLC信号
                await _signalSemaphore.WaitAsync(cancellationToken);
                
                if (_pendingSignals.TryDequeue(out var signal))
                {
                    // 在独立任务中处理信号，避免阻塞
                    _ = Task.Run(async () => await ProcessSingleSignalAsync(signal, cancellationToken), cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "📸 [相机模拟器] 处理信号队列时发生错误");
                await Task.Delay(1000, cancellationToken);
            }
        }
        
        Log.Information("📸 [相机模拟器] 信号处理器停止");
    }
    
    /// <summary>
    /// 处理单个PLC信号
    /// </summary>
    private async Task ProcessSingleSignalAsync(PlcSignal signal, CancellationToken cancellationToken)
    {
        try
        {
            // 计算延迟时间
            var actualDelay = _random.Next(_minDelayMs, _maxDelayMs + 1);
            var delayStart = DateTimeOffset.UtcNow;
            
            // 模拟相机处理延迟
            await Task.Delay(actualDelay, cancellationToken);
            
            var delayEnd = DateTimeOffset.UtcNow;
            var actualDelayMs = (delayEnd - delayStart).TotalMilliseconds;
            _delayMeasurements.Enqueue(actualDelayMs);
            
            // 限制延迟队列大小
            while (_delayMeasurements.Count > 1000)
            {
                _delayMeasurements.TryDequeue(out _);
            }
            
            Log.Information("📸 [相机模拟器] 延迟完成: 序号={PackageNumber}, 实际延迟={ActualDelay:F0}ms, PLC时间={PlcTime}, 拍照时间={CaptureTime}", 
                signal.PackageNumber, actualDelayMs, signal.Timestamp.ToString("HH:mm:ss.fff"), delayEnd.ToString("HH:mm:ss.fff"));
            
            // 发送相机数据到所有连接的客户端
            await SendCameraDataToClientsAsync(signal, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 正常取消
        }
        catch (Exception ex)
        {
            Log.Error(ex, "📸 [相机模拟器] 处理信号时发生错误: 序号={PackageNumber}", signal.PackageNumber);
        }
    }
    
    /// <summary>
    /// 发送相机数据到所有连接的客户端
    /// </summary>
    private async Task SendCameraDataToClientsAsync(PlcSignal signal, CancellationToken cancellationToken)
    {
        List<TcpClient> clients;
        lock (_clientsLock)
        {
            clients = new List<TcpClient>(_connectedClients);
        }
        
        if (clients.Count == 0)
        {
            Log.Warning("📸 [相机模拟器] 没有连接的客户端，数据将被丢弃: 序号={PackageNumber}", signal.PackageNumber);
            Interlocked.Increment(ref _totalSent);
            Interlocked.Increment(ref _totalFailed);
            return;
        }
        
        // 生成相机数据
        var cameraData = GenerateCameraData(signal);
        var dataBytes = Encoding.UTF8.GetBytes(cameraData);
        
        var sendTasks = clients.Select(async client =>
        {
            try
            {
                if (!client.Connected) return false;
                
                var stream = client.GetStream();
                var sendStart = DateTime.UtcNow;
                
                await stream.WriteAsync(dataBytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                
                var sendDuration = (DateTime.UtcNow - sendStart).TotalMilliseconds;
                var totalTime = (DateTime.UtcNow - signal.Timestamp).TotalMilliseconds;
                
                Log.Information("📸 [相机模拟器] 发送成功: 序号={PackageNumber}, 条码={Barcode}, 客户端={Client}, 发送={SendTime:F0}ms, 总时间={TotalTime:F0}ms", 
                    signal.PackageNumber, signal.Barcode, client.Client.RemoteEndPoint, sendDuration, totalTime);
                
                if (sendDuration > 100)
                {
                    Log.Warning("📸 [相机模拟器] ⚠️ 发送延迟高: {Duration:F0}ms", sendDuration);
                }
                
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "📸 [相机模拟器] 发送到客户端失败: 序号={PackageNumber}, 客户端={Client}", 
                    signal.PackageNumber, client.Client.RemoteEndPoint);
                return false;
            }
        });
        
        var results = await Task.WhenAll(sendTasks);
        var successCount = results.Count(r => r);
        
        Interlocked.Increment(ref _totalSent);
        if (successCount > 0)
        {
            Interlocked.Increment(ref _totalSuccessful);
        }
        else
        {
            Interlocked.Increment(ref _totalFailed);
        }
    }
    
    /// <summary>
    /// 生成相机数据
    /// </summary>
    private string GenerateCameraData(PlcSignal signal)
    {
        // 使用PLC信号中的条码，确保数据一致性
        var barcode = signal.Barcode;
        
        // 生成随机的物理属性
        var weight = _random.NextSingle() * 10 + 0.1f; // 0.1-10.1 kg
        var length = _random.NextDouble() * 50 + 10; // 10-60 cm
        var width = _random.NextDouble() * 30 + 10;  // 10-40 cm  
        var height = _random.NextDouble() * 20 + 5;  // 5-25 cm
        var volume = length * width * height / 1000; // 转换为升
        
        // 【协议修正】使用秒级时间戳，符合实际相机设备协议
        var sendTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        
        Log.Information("📸 [相机模拟器] 生成数据: 序号={PackageNumber}, 条码={Barcode}, 发送时间戳={SendTimestamp}(秒)", 
            signal.PackageNumber, barcode, sendTimestamp);
        
        // 【协议格式】7个字段: {code},{weight},{length},{width},{height},{volume},{sendTimestamp(秒)};
        return $"{barcode},{weight:F1},{length:F1},{width:F1},{height:F1},{volume:F2},{sendTimestamp};";
    }

    /// <summary>
    /// 获取延迟统计信息
    /// </summary>
    public (double Average, double Min, double Max, int Count) GetDelayStatistics()
    {
        var delays = new List<double>();
        
        while (_delayMeasurements.TryDequeue(out var delay))
        {
            delays.Add(delay);
        }
        
        // 将数据放回队列
        foreach (var delay in delays)
        {
            _delayMeasurements.Enqueue(delay);
        }
        
        if (delays.Count == 0)
        {
            return (0, 0, 0, 0);
        }
        
        return (delays.Average(), delays.Min(), delays.Max(), delays.Count);
    }
    
    /// <summary>
    /// 停止相机模拟器
    /// </summary>
    public void Stop()
    {
        _isRunning = false;
        _signalSemaphore.Release(); // 释放等待的处理器
        
        // 关闭所有客户端连接
        lock (_clientsLock)
        {
            foreach (var client in _connectedClients)
            {
                try
                {
                    client.Close();
                }
                catch { }
            }
            _connectedClients.Clear();
        }
        
        _listener?.Stop();
        Log.Information("📸 [相机模拟器] 收到停止信号");
    }
    
    public void Dispose()
    {
        Stop();
        _signalSemaphore?.Dispose();
        _listener?.Stop();
        GC.SuppressFinalize(this);
    }

    public static async Task Main(string[] args)
    {
        Console.Title = "增强版协同相机与PLC模拟器";
        _cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _cts.Cancel();
        };

        // 【关键改进】使用更健壮的具名参数解析
        var cameraPort = int.Parse(GetArgValue(args, "--camera-port", "20011"));
        var plcIp = GetArgValue(args, "--plc-ip", "127.0.0.1");
        var plcPort = int.Parse(GetArgValue(args, "--plc-port", "20010"));
        var sendIntervalMs = int.Parse(GetArgValue(args, "--interval", "20"));
        
        Console.WriteLine("--- 模拟器配置 ---");
        Console.WriteLine($"相机服务监听端口: {cameraPort} (使用 --camera-port 重写)");
        Console.WriteLine($"PLC服务目标: {plcIp}:{plcPort} (使用 --plc-ip, --plc-port 重写)");
        Console.WriteLine($"发送间隔: {sendIntervalMs} ms (约 {60000.0 / sendIntervalMs:F1} 次/分钟, 使用 --interval 重写)");
        Console.WriteLine("--------------------");
        Console.WriteLine("按 Ctrl+C 停止模拟器。");

        try
        {
            // 启动相机服务器
            var cameraListenTask = StartCameraServer(cameraPort, _cts.Token);
            var cameraBroadcastTask = BroadcastCameraData(_cts.Token);
            
            // 【新增】连接到PLC服务器
            await ConnectToPlcAsync(plcIp, plcPort, _cts.Token);
            StartListeningToPlc(_cts.Token);

            // 启动协同数据发送任务
            var coordinatedSendTask = SendCoordinatedData(sendIntervalMs, _cts.Token);
            
            await Task.WhenAll(cameraListenTask, cameraBroadcastTask, coordinatedSendTask);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\n模拟器正在停止...");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n模拟器发生致命错误: {ex.Message}");
            Console.ResetColor();
        }
        finally
        {
            // 清理资源
            _cameraListener?.Stop();
            foreach (var client in CameraClients)
            {
                client.Close();
            }
            _plcClient?.Close();
            Console.WriteLine("所有资源已释放，模拟器已关闭。");
        }
    }
    
    /// <summary>
    /// 【新增】从命令行参数数组中安全地获取具名参数的值
    /// </summary>
    private static string GetArgValue(string[] args, string argName, string defaultValue)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(argName, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return defaultValue;
    }

    private static async Task StartCameraServer(int port, CancellationToken token)
    {
        // ... existing code ...
        _cameraListener = new TcpListener(IPAddress.Any, port);
        _cameraListener.Start();
        Console.WriteLine($"[相机服务] 正在监听端口 {port}...");

        while (!token.IsCancellationRequested)
        {
            try
            {
                var client = await _cameraListener.AcceptTcpClientAsync(token);
                lock (CameraClients)
                {
                    CameraClients.Add(client);
                }
                var clientEp = client.Client.RemoteEndPoint;
                Console.WriteLine($"[相机服务] 接受客户端连接: {clientEp}");
            }
            catch (OperationCanceledException)
            {
                // 这是正常的关闭流程
                break;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[相机服务] 接受连接时发生错误: {ex.Message}");
                Console.ResetColor();
            }
        }
        Console.WriteLine("[相机服务] 监听已停止。");
    }

    private static async Task BroadcastCameraData(CancellationToken token)
    {
        await foreach (var data in CameraDataChannel.Reader.ReadAllAsync(token))
        {
            var dataBytes = Encoding.UTF8.GetBytes(data);
            List<TcpClient> clientsToBroadcast;
            List<TcpClient> disconnectedClients = new();

            // 【关键修复 CS1996】在lock中只复制列表，不在lock中await
            lock (CameraClients)
            {
                clientsToBroadcast = new List<TcpClient>(CameraClients);
            }

            foreach (var client in clientsToBroadcast)
            {
                if (!client.Connected)
                {
                    disconnectedClients.Add(client);
                    continue;
                }

                try
                {
                    var stream = client.GetStream();
                    await stream.WriteAsync(dataBytes, token);
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"[相机服务] 发送数据到 {client.Client.RemoteEndPoint} 失败: {ex.Message}");
                    Console.ResetColor();
                    disconnectedClients.Add(client);
                }
            }

            // 移除已断开的客户端
            if (disconnectedClients.Count > 0)
            {
                lock (CameraClients)
                {
                    foreach (var disconnected in disconnectedClients)
                    {
                        CameraClients.Remove(disconnected);
                        disconnected.Close();
                    }
                }
            }
        }
        Console.WriteLine("[相机服务] 数据广播已停止。");
    }

    /// <summary>
    /// 【新增】连接到PLC服务器
    /// </summary>
    private static async Task ConnectToPlcAsync(string ip, int port, CancellationToken token)
    {
        Console.WriteLine($"[PLC客户端] 正在尝试连接到 {ip}:{port}...");
        _plcClient = new TcpClient();

        while (!token.IsCancellationRequested)
        {
            try
            {
                await _plcClient.ConnectAsync(ip, port, token);
                _plcStream = _plcClient.GetStream();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[PLC客户端] ✅ 成功连接到PLC服务器: {ip}:{port}");
                Console.ResetColor();
                return; // 连接成功，退出循环
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[PLC客户端] 连接操作被取消。");
                break;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[PLC客户端] 连接失败: {ex.Message}。将在5秒后重试...");
                Console.ResetColor();
                await Task.Delay(5000, token);
                _plcClient?.Dispose(); // 销毁旧实例
                _plcClient = new TcpClient(); // 创建新实例以备重试
            }
        }
    }

    /// <summary>
    /// 【新增】启动一个后台任务来监听PLC服务器的返回指令
    /// </summary>
    private static void StartListeningToPlc(CancellationToken token)
    {
        Task.Run(async () =>
        {
            if (_plcStream == null) return;
            Console.WriteLine("[PLC客户端] 开始监听返回指令...");
            
            var buffer = new byte[8]; // PLC指令固定8字节
            var packageIndex = 0;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var bytesRead = await _plcStream.ReadAsync(buffer.AsMemory(packageIndex, buffer.Length - packageIndex), token);
                    if (bytesRead == 0)
                    {
                        Console.WriteLine("[PLC客户端] 服务器关闭了连接。");
                        break;
                    }

                    packageIndex += bytesRead;
                    if (packageIndex < buffer.Length)
                    {
                        continue; // 未接收完整
                    }

                    packageIndex = 0; // 重置索引
                    
                    // 验证并处理收到的完整数据包
                    ProcessPlcFeedback(buffer);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常关闭
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[PLC客户端] 监听时发生错误: {ex.Message}");
                Console.ResetColor();
            }
            finally
            {
                Console.WriteLine("[PLC客户端] 监听已停止。");
            }

        }, token);
    }
    
    /// <summary>
    /// 【新增】处理从PLC服务器收到的反馈指令
    /// </summary>
    private static void ProcessPlcFeedback(byte[] data)
    {
        // 验证数据包: 起始码 0xF9, 功能码 0x11, 校验和 0xFF
        if (data.Length != 8 || data[0] != 0xF9 || data[1] != 0x11 || data[7] != 0xFF)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[PLC反馈] 收到无效数据包: {BitConverter.ToString(data)}");
            Console.ResetColor();
            return;
        }

        var packageNumber = (ushort)(data[2] << 8 | data[3]);
        var chute = data[6];

        // 尝试从字典中找到对应的发送记录并计算时间差
        if (SentPlcSignals.TryRemove(packageNumber, out var sentTime))
        {
            var rtt = DateTimeOffset.UtcNow - sentTime;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[PLC反馈] 收到指令: 序号={packageNumber}, 格口={chute}。往返时延: {rtt.TotalMilliseconds:F0} ms");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[PLC反馈] 收到未知序号 {packageNumber} 的指令，可能已超时或重复。");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// 【新增】发送PLC触发信号
    /// </summary>
    private static async Task SendPlcTriggerSignalAsync(ushort packageNumber, CancellationToken token)
    {
        if (_plcStream == null || !_plcClient?.Connected == true)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[PLC客户端] 未连接，无法发送触发信号。");
            Console.ResetColor();
            return;
        }

        // 协议格式: 起始码(0xF9), 功能码(0x10), 包裹序号(2字节), 预留(2字节), 预留(1字节), 校验和(0xFF)
        var command = new byte[8];
        command[0] = 0xF9; // 起始码
        command[1] = 0x10; // 功能码: 接收包裹序号
        command[2] = (byte)(packageNumber >> 8 & 0xFF); // 包裹序号高字节
        command[3] = (byte)(packageNumber & 0xFF);     // 包裹序号低字节
        command[4] = 0x00; // 预留
        command[5] = 0x00; // 预留
        command[6] = 0x00; // 预留
        command[7] = 0xFF; // 校验和

        try
        {
            // 记录发送时间
            SentPlcSignals[packageNumber] = DateTimeOffset.UtcNow;
            await _plcStream.WriteAsync(command, token);
            await _plcStream.FlushAsync(token);

            Console.WriteLine($"[PLC客户端] -> 发送触发信号: 序号={packageNumber}");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[PLC客户端] 发送触发信号 {packageNumber} 失败: {ex.Message}");
            Console.ResetColor();
            SentPlcSignals.TryRemove(packageNumber, out _); // 发送失败则移除记录
        }
    }
    
    private static async Task SendCoordinatedData(int intervalMilliseconds, CancellationToken token)
    {
        var random = new Random();
        Console.WriteLine("\n--- 开始协同发送 PLC信号 和 相机数据 ---");
        
        while (!token.IsCancellationRequested)
        {
            try
            {
                // 1. 【新增】发送PLC触发信号
                var packageNumber = _currentPlcPackageNumber++;
                await SendPlcTriggerSignalAsync(packageNumber, token);
                
                // 2. 模拟PLC和相机之间的物理延迟
                await Task.Delay(random.Next(100, 300), token);

                // 3. 发送相机数据
                var barcode = $"PKG{DateTime.Now:HHmmssfff}{random.Next(100, 999)}";
                var weight = random.Next(1, 5000) / 1000.0f;
                var length = random.Next(10, 50) / 10.0;
                var width = random.Next(10, 40) / 10.0;
                var height = random.Next(5, 30) / 10.0;
                var volume = length * width * height;
                var sendTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                // 协议格式: {code},{weight},{length},{width},{height},{volume},{sendTimestamp(秒)};
                var cameraData = $"{barcode},{weight:F3},{length:F2},{width:F2},{height:F2},{volume:F2},{sendTimestamp};";
                
                await CameraDataChannel.Writer.WriteAsync(cameraData, token);
                Console.WriteLine($"[相机服务] -> 发送相机数据: 条码={barcode}");

                // 4. 等待下一个循环
                await Task.Delay(intervalMilliseconds, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"发送协同数据时发生错误: {ex.Message}");
                Console.ResetColor();
                await Task.Delay(1000, token); // 出错后稍作等待
            }
        }
    }
} 