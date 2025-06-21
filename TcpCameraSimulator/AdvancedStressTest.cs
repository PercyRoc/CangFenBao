using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TcpCameraSimulator;

/// <summary>
/// 高级TCP相机压力测试工具 - 支持更详细的性能分析和监控
/// </summary>
public class AdvancedStressTest
{
    private readonly StressTestConfig _config;
    private readonly ConcurrentQueue<double> _latencyMeasurements = new();
    private readonly ConcurrentDictionary<int, ClientStats> _clientStats = new();
    private volatile bool _isRunning = true;
    private volatile int _totalSent = 0;
    private volatile int _totalSuccessful = 0;
    private volatile int _totalFailed = 0;
    private volatile int _totalConnections = 0;
    private volatile int _totalDisconnections = 0;
    
    public AdvancedStressTest(StressTestConfig config)
    {
        _config = config;
    }
    
    public async Task RunAsync()
    {
        Console.WriteLine("🔥 高级TCP相机压力测试启动");
        Console.WriteLine("================================");
        Console.WriteLine($"目标服务器: {_config.ServerHost}:{_config.ServerPort}");
        Console.WriteLine($"并发客户端: {_config.ConcurrentClients}");
        Console.WriteLine($"发送模式: {_config.SendMode}");
        Console.WriteLine($"包裹频率: {_config.PackagesPerSecond} 包/秒");
        Console.WriteLine($"批量大小: {_config.BatchSize}");
        Console.WriteLine($"测试时长: {_config.TestDurationSeconds} 秒");
        Console.WriteLine($"背压测试: {(_config.EnableBackpressureTest ? "启用" : "禁用")}");
        Console.WriteLine();
        
        // 启动监控任务
        var monitoringTask = StartAdvancedMonitoring();
        var latencyAnalysisTask = StartLatencyAnalysis();
        
        // 启动多个并发客户端
        var clientTasks = new Task[_config.ConcurrentClients];
        var cancellationTokenSource = new CancellationTokenSource();
        
        for (int i = 0; i < _config.ConcurrentClients; i++)
        {
            int clientId = i + 1;
            _clientStats[clientId] = new ClientStats();
            
            if (_config.SendMode == SendMode.Burst)
            {
                clientTasks[i] = RunBurstClientAsync(clientId, cancellationTokenSource.Token);
            }
            else
            {
                clientTasks[i] = RunSteadyClientAsync(clientId, cancellationTokenSource.Token);
            }
        }
        
        Console.WriteLine("按 'q' 键提前停止测试，按 's' 查看详细统计...");
        Console.WriteLine();
        
        // 测试控制循环
        var testEndTime = DateTime.UtcNow.AddSeconds(_config.TestDurationSeconds);
        while (DateTime.UtcNow < testEndTime && _isRunning)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(true);
                switch (key.KeyChar)
                {
                    case 'q':
                    case 'Q':
                        Console.WriteLine("用户请求停止测试...");
                        _isRunning = false;
                        break;
                    case 's':
                    case 'S':
                        PrintDetailedStats();
                        break;
                }
            }
            await Task.Delay(100);
        }
        
        // 停止测试
        _isRunning = false;
        cancellationTokenSource.Cancel();
        
        try
        {
            await Task.WhenAll(clientTasks);
        }
        catch (OperationCanceledException) { }
        
        // 显示最终报告
        await PrintFinalReport();
    }
    
    private async Task RunSteadyClientAsync(int clientId, CancellationToken cancellationToken)
    {
        int sendIntervalMs = 1000 / _config.PackagesPerSecond;
        var stats = _clientStats[clientId];
        
        Console.WriteLine($"[客户端 {clientId}] 稳定模式启动，发送间隔: {sendIntervalMs}ms");
        
        while (_isRunning && !cancellationToken.IsCancellationRequested)
        {
            await RunSingleConnection(clientId, stats, sendIntervalMs, cancellationToken);
            
            if (_isRunning)
            {
                await Task.Delay(1000, cancellationToken); // 重连延迟
            }
        }
    }
    
    private async Task RunBurstClientAsync(int clientId, CancellationToken cancellationToken)
    {
        var stats = _clientStats[clientId];
        
        Console.WriteLine($"[客户端 {clientId}] 突发模式启动，批量大小: {_config.BatchSize}");
        
        while (_isRunning && !cancellationToken.IsCancellationRequested)
        {
            await RunSingleConnection(clientId, stats, 0, cancellationToken, true);
            
            if (_isRunning)
            {
                await Task.Delay(2000, cancellationToken); // 突发模式间隔更长
            }
        }
    }
    
    private async Task RunSingleConnection(int clientId, ClientStats stats, int intervalMs, 
        CancellationToken cancellationToken, bool burstMode = false)
    {
        TcpClient? client = null;
        NetworkStream? stream = null;
        
        try
        {
            // 建立连接
            client = new TcpClient();
            var connectStart = DateTime.UtcNow;
            await client.ConnectAsync(_config.ServerHost, _config.ServerPort);
            var connectDuration = (DateTime.UtcNow - connectStart).TotalMilliseconds;
            
            stream = client.GetStream();
            stats.Connections++;
            Interlocked.Increment(ref _totalConnections);
            
            Console.WriteLine($"[客户端 {clientId}] 连接成功，耗时: {connectDuration:F0}ms");
            
            // 发送数据
            var packagesInThisConnection = 0;
            var connectionStart = DateTime.UtcNow;
            
            while (_isRunning && !cancellationToken.IsCancellationRequested && client.Connected)
            {
                if (burstMode)
                {
                    // 突发模式：快速发送一批数据
                    await SendBurstPackages(clientId, stream, stats, cancellationToken);
                    await Task.Delay(1000, cancellationToken); // 突发后休息
                }
                else
                {
                    // 稳定模式：按固定间隔发送
                    await SendSinglePackage(clientId, stream, stats, cancellationToken);
                    if (intervalMs > 0)
                    {
                        await Task.Delay(intervalMs, cancellationToken);
                    }
                }
                
                packagesInThisConnection++;
                
                // 背压测试：定期检查连接状态
                if (_config.EnableBackpressureTest && packagesInThisConnection % 100 == 0)
                {
                    if (!await TestConnectionHealth(stream))
                    {
                        Console.WriteLine($"[客户端 {clientId}] 背压测试失败，连接不健康");
                        break;
                    }
                }
            }
            
            var connectionDuration = (DateTime.UtcNow - connectionStart).TotalSeconds;
            stats.TotalConnectionTime += connectionDuration;
        }
        catch (Exception ex)
        {
            stats.ConnectionErrors++;
            Console.WriteLine($"[客户端 {clientId}] 连接错误: {ex.Message}");
        }
        finally
        {
            stats.Disconnections++;
            Interlocked.Increment(ref _totalDisconnections);
            stream?.Dispose();
            client?.Close();
        }
    }
    
    private async Task SendBurstPackages(int clientId, NetworkStream stream, ClientStats stats, 
        CancellationToken cancellationToken)
    {
        var burstStart = DateTime.UtcNow;
        var packagesInBurst = 0;
        
        for (int i = 0; i < _config.BatchSize && _isRunning; i++)
        {
            try
            {
                await SendSinglePackage(clientId, stream, stats, cancellationToken);
                packagesInBurst++;
            }
            catch
            {
                break; // 发送失败，退出突发
            }
        }
        
        var burstDuration = (DateTime.UtcNow - burstStart).TotalMilliseconds;
        var burstRate = packagesInBurst / (burstDuration / 1000.0);
        
        Console.WriteLine($"[客户端 {clientId}] 突发完成: {packagesInBurst} 包, {burstDuration:F0}ms, {burstRate:F1} 包/秒");
    }
    
    private async Task SendSinglePackage(int clientId, NetworkStream stream, ClientStats stats, 
        CancellationToken cancellationToken)
    {
        var packageData = GeneratePackageData();
        var dataBytes = Encoding.UTF8.GetBytes(packageData);
        
        var sendStart = DateTime.UtcNow;
        await stream.WriteAsync(dataBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        var sendDuration = (DateTime.UtcNow - sendStart).TotalMilliseconds;
        
        // 记录性能指标
        _latencyMeasurements.Enqueue(sendDuration);
        stats.PackagesSent++;
        stats.TotalSendTime += sendDuration;
        
        Interlocked.Increment(ref _totalSent);
        Interlocked.Increment(ref _totalSuccessful);
        
        // 限制延迟队列大小，避免内存无限增长
        while (_latencyMeasurements.Count > 10000)
        {
            _latencyMeasurements.TryDequeue(out _);
        }
        
        if (sendDuration > 100)
        {
            Console.WriteLine($"[客户端 {clientId}] ⚠️ 发送延迟高: {sendDuration:F0}ms");
            stats.HighLatencyPackages++;
        }
    }
    
    private static async Task<bool> TestConnectionHealth(NetworkStream stream)
    {
        try
        {
            // 发送一个心跳包测试连接
            var heartbeat = Encoding.UTF8.GetBytes("HEARTBEAT@");
            await stream.WriteAsync(heartbeat);
            await stream.FlushAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    private string GeneratePackageData()
    {
        var random = Random.Shared;
        var barcode = $"T{random.Next(10000000, 99999999)}{(char)('A' + random.Next(0, 26))}";
        var weight = random.NextSingle() * 10 + 0.1f;
        var length = random.NextDouble() * 50 + 10;
        var width = random.NextDouble() * 30 + 10;
        var height = random.NextDouble() * 20 + 5;
        var volume = length * width * height / 1000;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        
        return $"{barcode},{weight:F1},{length:F1},{width:F1},{height:F1},{volume:F2},{timestamp}@";
    }
    
    private Task StartAdvancedMonitoring()
    {
        return Task.Run(async () =>
        {
            var lastSent = 0;
            var lastTime = DateTime.UtcNow;
            
            while (_isRunning)
            {
                await Task.Delay(3000); // 每3秒报告一次
                
                var currentTime = DateTime.UtcNow;
                var currentSent = _totalSent;
                var deltaTime = (currentTime - lastTime).TotalSeconds;
                var deltaSent = currentSent - lastSent;
                var rate = deltaTime > 0 ? deltaSent / deltaTime : 0;
                
                Console.WriteLine($"📊 [实时] 速率: {rate:F1} 包/秒 | 总计: {currentSent} | 连接: {_totalConnections} | 断开: {_totalDisconnections}");
                
                lastSent = currentSent;
                lastTime = currentTime;
            }
        });
    }
    
    private Task StartLatencyAnalysis()
    {
        return Task.Run(async () =>
        {
            while (_isRunning)
            {
                await Task.Delay(10000); // 每10秒分析一次延迟
                
                if (_latencyMeasurements.IsEmpty) continue;
                
                var latencies = new List<double>();
                while (_latencyMeasurements.TryDequeue(out var latency))
                {
                    latencies.Add(latency);
                }
                
                if (latencies.Count > 0)
                {
                    latencies.Sort();
                    var p50 = latencies[latencies.Count / 2];
                    var p95 = latencies[(int)(latencies.Count * 0.95)];
                    var p99 = latencies[(int)(latencies.Count * 0.99)];
                    var avg = latencies.Average();
                    var max = latencies.Max();
                    
                    Console.WriteLine($"📈 [延迟分析] 样本: {latencies.Count} | 平均: {avg:F1}ms | P50: {p50:F1}ms | P95: {p95:F1}ms | P99: {p99:F1}ms | 最大: {max:F1}ms");
                }
            }
        });
    }
    
    private void PrintDetailedStats()
    {
        Console.WriteLine();
        Console.WriteLine("=== 详细统计信息 ===");
        
        foreach (var kvp in _clientStats)
        {
            var clientId = kvp.Key;
            var stats = kvp.Value;
            var avgSendTime = stats.PackagesSent > 0 ? stats.TotalSendTime / stats.PackagesSent : 0;
            var avgConnTime = stats.Connections > 0 ? stats.TotalConnectionTime / stats.Connections : 0;
            
            Console.WriteLine($"客户端 {clientId}: 发送={stats.PackagesSent}, 连接={stats.Connections}, 平均发送={avgSendTime:F1}ms, 平均连接时间={avgConnTime:F1}s, 高延迟包={stats.HighLatencyPackages}");
        }
        Console.WriteLine("==================");
        Console.WriteLine();
    }
    
    private async Task PrintFinalReport()
    {
        Console.WriteLine();
        Console.WriteLine("🎯 === 最终测试报告 ===");
        Console.WriteLine($"测试持续时间: {_config.TestDurationSeconds} 秒");
        Console.WriteLine($"总发送包裹: {_totalSent}");
        Console.WriteLine($"发送成功: {_totalSuccessful}");
        Console.WriteLine($"发送失败: {_totalFailed}");
        Console.WriteLine($"成功率: {(_totalSent > 0 ? (double)_totalSuccessful / _totalSent * 100 : 0):F2}%");
        Console.WriteLine($"平均吞吐量: {(_totalSent / (double)_config.TestDurationSeconds):F2} 包/秒");
        Console.WriteLine($"总连接数: {_totalConnections}");
        Console.WriteLine($"总断开数: {_totalDisconnections}");
        
        // 客户端统计汇总
        var totalHighLatency = _clientStats.Values.Sum(s => s.HighLatencyPackages);
        var totalConnectionErrors = _clientStats.Values.Sum(s => s.ConnectionErrors);
        
        Console.WriteLine($"高延迟包裹: {totalHighLatency}");
        Console.WriteLine($"连接错误: {totalConnectionErrors}");
        Console.WriteLine("=======================");
        
        PrintDetailedStats();
        
        await Task.CompletedTask;
    }
}

public class StressTestConfig
{
    public string ServerHost { get; set; } = "127.0.0.1";
    public int ServerPort { get; set; } = 20011;
    public int ConcurrentClients { get; set; } = 3;
    public int PackagesPerSecond { get; set; } = 10;
    public int TestDurationSeconds { get; set; } = 60;
    public SendMode SendMode { get; set; } = SendMode.Steady;
    public int BatchSize { get; set; } = 10;
    public bool EnableBackpressureTest { get; set; } = true;
}

public enum SendMode
{
    Steady,  // 稳定发送
    Burst    // 突发发送
}

public class ClientStats
{
    public int PackagesSent { get; set; }
    public int Connections { get; set; }
    public int Disconnections { get; set; }
    public int ConnectionErrors { get; set; }
    public int HighLatencyPackages { get; set; }
    public double TotalSendTime { get; set; }
    public double TotalConnectionTime { get; set; }
} 