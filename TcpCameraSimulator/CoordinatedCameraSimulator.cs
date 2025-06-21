using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using System.Linq;

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
} 