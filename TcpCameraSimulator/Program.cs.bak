using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Serilog;

namespace TcpCameraSimulator;

/// <summary>
/// TCP相机模拟器 - 用于压测相机服务的性能和稳定性
/// </summary>
class Program
{
    private static readonly Random Random = new();
    private static volatile bool _isRunning = true;
    private static volatile int _totalSent = 0;
    private static volatile int _totalSuccessful = 0;
    private static volatile int _totalFailed = 0;
    private static readonly object _statsLock = new();
    
    // 高级压测相关
    private static readonly ConcurrentQueue<double> _latencyMeasurements = new();
    private static volatile int _totalConnections = 0;
    private static volatile int _totalDisconnections = 0;
    
    // PLC和相机协调模式相关
    private static PlcSimulator? _plcSimulator = null;
    private static CoordinatedCameraSimulator? _coordinatedCameraSimulator = null;
    
    static async Task Main(string[] args)
    {
        // 初始化日志
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateLogger();
            
        Console.WriteLine("🚀 TCP相机数据模拟器 - 压力测试工具 v2.1");
        Console.WriteLine("=============================================");
        
        // 如果没有命令行参数，启动交互式协调模式
        if (args.Length == 0)
        {
            await StartInteractiveCoordinatedModeAsync();
            return;
        }
        
        // 解析命令行参数
        var config = ParseArguments(args);
        
        Console.WriteLine($"目标服务器: PLC={config.PlcHost}:{config.PlcPort}, 相机={config.ServerHost}:{config.ServerPort}");
        Console.WriteLine($"测试模式: {GetTestModeDescription(config)}");
        Console.WriteLine($"测试时长: {config.TestDurationSeconds} 秒");
        
        if (config.EnableCoordinatedMode)
        {
            Console.WriteLine($"协调模式: PLC频率={config.PackagesPerSecond} 包/秒, 相机延迟={config.CameraDelayMin}-{config.CameraDelayMax}ms");
        }
        else if (config.EnableStressMode)
        {
            Console.WriteLine($"压测模式: 并发客户端={config.ConcurrentClients}, 突发批量={config.BurstSize} 包/批");
        }
        else
        {
            Console.WriteLine($"标准模式: 并发客户端={config.ConcurrentClients}, 发送频率={config.PackagesPerSecond} 包/秒");
        }
        Console.WriteLine();
        
        await RunTestWithConfigAsync(config);
    }
    
    private static async Task StartInteractiveCoordinatedModeAsync()
    {
        Console.WriteLine("🔧📸 交互式协调模式启动");
        Console.WriteLine("===================");
        Console.WriteLine();
        Console.WriteLine("本模式将模拟真实的PLC+相机协调场景，推荐用于背压验证");
        Console.WriteLine();
        
        // 获取用户配置
        var config = new TestConfig { EnableCoordinatedMode = true };
        
        Console.Write($"PLC服务器地址 (默认: {config.PlcHost}): ");
        var plcHost = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(plcHost)) config.PlcHost = plcHost;
        
        Console.Write($"PLC服务器端口 (默认: {config.PlcPort}): ");
        var plcPortInput = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(plcPortInput) && int.TryParse(plcPortInput, out var plcPort))
            config.PlcPort = plcPort;
        
        Console.Write($"相机服务器地址 (默认: {config.ServerHost}): ");
        var cameraHost = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(cameraHost)) config.ServerHost = cameraHost;
        
        Console.Write($"相机服务器端口 (默认: {config.ServerPort}): ");
        var cameraPortInput = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(cameraPortInput) && int.TryParse(cameraPortInput, out var cameraPort))
            config.ServerPort = cameraPort;
        
        Console.Write($"PLC信号频率/包每秒 (默认: {config.PackagesPerSecond}): ");
        var rateInput = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(rateInput) && int.TryParse(rateInput, out var rate))
            config.PackagesPerSecond = rate;
        
        Console.Write($"测试时长/秒 (默认: {config.TestDurationSeconds}): ");
        var durationInput = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(durationInput) && int.TryParse(durationInput, out var duration))
            config.TestDurationSeconds = duration;
        
        Console.WriteLine();
        Console.WriteLine("📋 配置确认:");
        Console.WriteLine($"  PLC服务器: {config.PlcHost}:{config.PlcPort}");
        Console.WriteLine($"  相机服务器: {config.ServerHost}:{config.ServerPort}");
        Console.WriteLine($"  信号频率: {config.PackagesPerSecond} 包/秒");
        Console.WriteLine($"  相机延迟: {config.CameraDelayMin}-{config.CameraDelayMax}ms");
        Console.WriteLine($"  测试时长: {config.TestDurationSeconds} 秒");
        Console.WriteLine();
        
        Console.Write("确认开始测试? (Y/n): ");
        var confirm = Console.ReadLine()?.Trim().ToLower();
        if (confirm == "n" || confirm == "no")
        {
            Console.WriteLine("测试已取消");
            return;
        }
        
        Console.WriteLine();
        Console.WriteLine("🚀 开始协调模式测试...");
        Console.WriteLine();
        
        await RunTestWithConfigAsync(config);
    }
    
    private static async Task RunTestWithConfigAsync(TestConfig config)
    {
        // 启动统计显示任务
        var statsTask = StartAdvancedStatsReporter(config);
        var latencyTask = StartLatencyAnalyzer();
        
        var cancellationTokenSource = new CancellationTokenSource();
        Task[] clientTasks;
        
        if (config.EnableCoordinatedMode)
        {
            // 协调模式：PLC + 相机
            clientTasks = await StartCoordinatedModeAsync(config, cancellationTokenSource.Token);
        }
        else
        {
            // 传统模式：独立相机客户端
            clientTasks = StartTraditionalModeAsync(config, cancellationTokenSource.Token);
        }
        
        Console.WriteLine("控制台命令:");
        Console.WriteLine("  q - 退出测试");
        Console.WriteLine("  s - 显示详细统计");
        Console.WriteLine("  r - 重置统计数据");
        Console.WriteLine("  l - 显示延迟分析");
        if (config.EnableCoordinatedMode)
        {
            Console.WriteLine("  d - 显示延迟统计");
        }
        Console.WriteLine();
        
        // 等待测试完成或用户中断
        var testEndTime = DateTime.UtcNow.AddSeconds(config.TestDurationSeconds);
        
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
                        PrintDetailedStats(config);
                        break;
                    case 'r':
                    case 'R':
                        ResetStats();
                        break;
                    case 'l':
                    case 'L':
                        PrintLatencyAnalysis();
                        break;
                    case 'd':
                    case 'D':
                        if (config.EnableCoordinatedMode)
                        {
                            PrintDelayStatistics();
                        }
                        break;
                }
            }
            await Task.Delay(100);
        }
        
        // 停止所有任务
        _isRunning = false;
        cancellationTokenSource.Cancel();
        
        // 停止协调模式的模拟器
        _plcSimulator?.Stop();
        _coordinatedCameraSimulator?.Stop();
        
        try
        {
            await Task.WhenAll(clientTasks);
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        
        // 显示最终统计
        PrintFinalReport(config);
        
        // 清理资源
        _plcSimulator?.Dispose();
        _coordinatedCameraSimulator?.Dispose();
        
        Console.WriteLine("按任意键退出...");
        Console.ReadKey();
    }
    
    private static string GetTestModeDescription(TestConfig config)
    {
        if (config.EnableCoordinatedMode)
            return "PLC+相机协调模式";
        else if (config.EnableStressMode)
            return "高强度压测模式";
        else
            return "标准独立模式";
    }
    
    private static Task<Task[]> StartCoordinatedModeAsync(TestConfig config, CancellationToken cancellationToken)
    {
        Console.WriteLine("🔧📸 启动协调模式：PLC模拟器 + 相机模拟器");
        
        // 创建PLC模拟器
        _plcSimulator = new PlcSimulator(config.PlcHost, config.PlcPort);
        
        // 创建协调相机模拟器
        _coordinatedCameraSimulator = new CoordinatedCameraSimulator(
            config.ServerHost, config.ServerPort, 
            config.CameraDelayMin, config.CameraDelayMax);
        
        // 连接PLC信号到相机模拟器
        _plcSimulator.SignalSent += (sender, signal) =>
        {
            _coordinatedCameraSimulator.OnPlcSignalReceived(signal);
        };
        
        // 【新增】创建协调启动任务
        var coordinatedStartTask = Task.Run(async () =>
        {
            Console.WriteLine("⏳ 正在启动模拟设备，等待客户端连接...");
            
            // 1. 先启动相机模拟器（服务器模式）
            var cameraTask = _coordinatedCameraSimulator.StartAsync(cancellationToken);
            
            // 2. 等待相机模拟器有客户端连接
            Console.WriteLine("📸 相机模拟器已启动，等待客户端连接...");
            
            var maxWaitTime = TimeSpan.FromSeconds(30); // 最多等待30秒
            var waitStart = DateTime.UtcNow;
            var hasClientConnected = false;
            
            while (!cancellationToken.IsCancellationRequested && !hasClientConnected && 
                   (DateTime.UtcNow - waitStart) < maxWaitTime)
            {
                // 检查相机模拟器是否有客户端连接
                hasClientConnected = _coordinatedCameraSimulator.HasConnectedClients;
                
                if (!hasClientConnected)
                {
                    await Task.Delay(500, cancellationToken); // 每500ms检查一次
                    
                    // 每5秒提示一次
                    if ((DateTime.UtcNow - waitStart).TotalSeconds % 5 < 0.5)
                    {
                        Console.WriteLine($"⏳ 等待客户端连接中... (已等待 {(DateTime.UtcNow - waitStart).TotalSeconds:F0} 秒)");
                    }
                }
            }
            
            if (hasClientConnected)
            {
                Console.WriteLine("✅ 客户端已连接，开始发送PLC信号...");
                
                // 3. 启动PLC模拟器开始发送信号
                var plcTask = _plcSimulator.StartAsync(config.PackagesPerSecond, cancellationToken);
                
                // 返回两个任务
                return new[] { plcTask, cameraTask };
            }
            else if (cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine("❌ 测试被取消");
                return new[] { Task.CompletedTask, cameraTask };
            }
            else
            {
                Console.WriteLine("⚠️  等待超时，没有客户端连接。PLC模拟器将不会发送信号。");
                Console.WriteLine("   提示：请确保相机服务已启动并尝试连接到相机模拟器。");
                
                // 即使没有客户端连接，也返回任务以避免程序崩溃
                return new[] { Task.CompletedTask, cameraTask };
            }
        }, cancellationToken);
        
        return coordinatedStartTask;
    }
    
    private static Task[] StartTraditionalModeAsync(TestConfig config, CancellationToken cancellationToken)
    {
        Console.WriteLine("📸 启动传统模式：独立相机客户端");
        
        var clientTasks = new Task[config.ConcurrentClients];
        
        for (int i = 0; i < config.ConcurrentClients; i++)
        {
            int clientId = i + 1;
            if (config.EnableStressMode)
            {
                clientTasks[i] = RunStressClientAsync(clientId, config, cancellationToken);
            }
            else
            {
                clientTasks[i] = RunClientAsync(clientId, config.ServerHost, config.ServerPort, 
                    config.PackagesPerSecond, cancellationToken);
            }
        }
        
        return clientTasks;
    }
    
    private static TestConfig ParseArguments(string[] args)
    {
        var config = new TestConfig();
        
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLower())
            {
                case "--host":
                case "-h":
                    if (i + 1 < args.Length) config.ServerHost = args[++i];
                    break;
                case "--port":
                case "-p":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var port))
                        config.ServerPort = port;
                    break;
                case "--plc-host":
                    if (i + 1 < args.Length) config.PlcHost = args[++i];
                    break;
                case "--plc-port":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var plcPort))
                        config.PlcPort = plcPort;
                    break;
                case "--clients":
                case "-c":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var clients))
                        config.ConcurrentClients = clients;
                    break;
                case "--rate":
                case "-r":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var rate))
                        config.PackagesPerSecond = rate;
                    break;
                case "--duration":
                case "-d":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var duration))
                        config.TestDurationSeconds = duration;
                    break;
                case "--stress":
                case "-s":
                    config.EnableStressMode = true;
                    break;
                case "--burst":
                case "-b":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var burst))
                        config.BurstSize = burst;
                    break;
                case "--coordinated":
                case "--coord":
                    config.EnableCoordinatedMode = true;
                    break;
                case "--camera-delay-min":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var delayMin))
                        config.CameraDelayMin = delayMin;
                    break;
                case "--camera-delay-max":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var delayMax))
                        config.CameraDelayMax = delayMax;
                    break;
                case "--help":
                case "?":
                    PrintUsage();
                    Environment.Exit(0);
                    break;
            }
        }
        
        return config;
    }
    
    private static void PrintUsage()
    {
        Console.WriteLine("使用方法:");
        Console.WriteLine("  TcpCameraSimulator [选项]");
        Console.WriteLine();
        Console.WriteLine("选项:");
        Console.WriteLine("  -h, --host <地址>           相机服务器地址 (默认: 127.0.0.1)");
        Console.WriteLine("  -p, --port <端口>           相机服务器端口 (默认: 20011)");
        Console.WriteLine("      --plc-host <地址>       PLC服务器地址 (默认: 127.0.0.1)");
        Console.WriteLine("      --plc-port <端口>       PLC服务器端口 (默认: 20010)");
        Console.WriteLine("  -c, --clients <数量>        并发客户端数量 (默认: 3)");
        Console.WriteLine("  -r, --rate <频率>           每秒发送包裹数 (默认: 10)");
        Console.WriteLine("  -d, --duration <秒>         测试持续时间 (默认: 60)");
        Console.WriteLine("  -s, --stress                启用高强度压测模式");
        Console.WriteLine("  -b, --burst <数量>          突发模式每批发送数量 (默认: 20)");
        Console.WriteLine("      --coordinated           启用PLC+相机协调模式");
        Console.WriteLine("      --camera-delay-min <ms> 相机延迟最小值 (默认: 800)");
        Console.WriteLine("      --camera-delay-max <ms> 相机延迟最大值 (默认: 900)");
        Console.WriteLine("      --help                  显示此帮助信息");
        Console.WriteLine();
        Console.WriteLine("测试模式:");
        Console.WriteLine("  1. 标准模式: 独立相机客户端并发测试");
        Console.WriteLine("  2. 压测模式: 高强度突发负载测试");
        Console.WriteLine("  3. 协调模式: PLC信号触发 + 相机延迟响应 (真实场景模拟)");
        Console.WriteLine();
        Console.WriteLine("示例:");
        Console.WriteLine("  # 标准模式");
        Console.WriteLine("  TcpCameraSimulator --clients 5 --rate 20 --duration 120");
        Console.WriteLine();
        Console.WriteLine("  # 压测模式");
        Console.WriteLine("  TcpCameraSimulator --stress --burst 50 --clients 10");
        Console.WriteLine();
        Console.WriteLine("  # 协调模式（推荐用于背压测试）");
        Console.WriteLine("  TcpCameraSimulator --coordinated --rate 15 --camera-delay-min 800 --camera-delay-max 900");
        Console.WriteLine();
        Console.WriteLine("  # 自定义协调模式");
        Console.WriteLine("  TcpCameraSimulator --coordinated --plc-host 192.168.1.100 --plc-port 502 --host 192.168.1.101");
    }
    
    private static void PrintDelayStatistics()
    {
        if (_coordinatedCameraSimulator == null) return;
        
        var (avg, min, max, count) = _coordinatedCameraSimulator.GetDelayStatistics();
        
        Console.WriteLine();
        Console.WriteLine("=== 相机延迟统计 ===");
        Console.WriteLine($"样本数量: {count}");
        Console.WriteLine($"平均延迟: {avg:F1}ms");
        Console.WriteLine($"最小延迟: {min:F1}ms");
        Console.WriteLine($"最大延迟: {max:F1}ms");
        Console.WriteLine("==================");
        Console.WriteLine();
    }
    
    private static async Task RunStressClientAsync(int clientId, TestConfig config, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[压测客户端 {clientId}] 启动高强度模式，突发大小: {config.BurstSize}");
        
        while (_isRunning && !cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            NetworkStream? stream = null;
            
            try
            {
                // 连接到服务器
                client = new TcpClient();
                var connectStart = DateTime.UtcNow;
                await client.ConnectAsync(config.ServerHost, config.ServerPort);
                var connectDuration = (DateTime.UtcNow - connectStart).TotalMilliseconds;
                
                stream = client.GetStream();
                Interlocked.Increment(ref _totalConnections);
                
                Console.WriteLine($"[压测客户端 {clientId}] 连接成功，耗时: {connectDuration:F0}ms");
                
                // 高强度突发发送
                while (_isRunning && !cancellationToken.IsCancellationRequested && client.Connected)
                {
                    var burstStart = DateTime.UtcNow;
                    var burstSuccessful = 0;
                    
                    // 突发发送一批数据
                    for (int i = 0; i < config.BurstSize && client.Connected; i++)
                    {
                        try
                        {
                            var packageData = GeneratePackageData();
                            var dataBytes = Encoding.UTF8.GetBytes(packageData);
                            
                            var sendStart = DateTime.UtcNow;
                            await stream.WriteAsync(dataBytes, cancellationToken);
                            await stream.FlushAsync(cancellationToken);
                            var sendDuration = (DateTime.UtcNow - sendStart).TotalMilliseconds;
                            
                            _latencyMeasurements.Enqueue(sendDuration);
                            burstSuccessful++;
                            
                            lock (_statsLock)
                            {
                                _totalSent++;
                                _totalSuccessful++;
                            }
                            
                            if (sendDuration > 100)
                            {
                                Console.WriteLine($"[压测客户端 {clientId}] ⚠️ 发送延迟高: {sendDuration:F0}ms");
                            }
                        }
                        catch (Exception ex) when (!(ex is OperationCanceledException))
                        {
                            lock (_statsLock)
                            {
                                _totalSent++;
                                _totalFailed++;
                            }
                            Console.WriteLine($"[压测客户端 {clientId}] 突发发送失败: {ex.Message}");
                            break;
                        }
                    }
                    
                    var burstDuration = (DateTime.UtcNow - burstStart).TotalMilliseconds;
                    var burstRate = burstSuccessful / (burstDuration / 1000.0);
                    
                    Console.WriteLine($"[压测客户端 {clientId}] 突发完成: {burstSuccessful}/{config.BurstSize}, {burstDuration:F0}ms, {burstRate:F1} 包/秒");
                    
                    // 突发间隔
                    await Task.Delay(1000, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[压测客户端 {clientId}] 连接失败: {ex.Message}");
                await Task.Delay(2000, cancellationToken); // 错误后等待更长时间
            }
            finally
            {
                Interlocked.Increment(ref _totalDisconnections);
                stream?.Dispose();
                client?.Close();
                
                if (_isRunning && !cancellationToken.IsCancellationRequested)
                {
                    Console.WriteLine($"[压测客户端 {clientId}] 连接断开，准备重连...");
                    await Task.Delay(1000, cancellationToken);
                }
            }
        }
        
        Console.WriteLine($"[压测客户端 {clientId}] 停止运行");
    }
    
    private static async Task RunClientAsync(int clientId, string host, int port, 
        int packagesPerSecond, CancellationToken cancellationToken)
    {
        int sendIntervalMs = 1000 / packagesPerSecond;
        int packagesSent = 0;
        int packagesSuccessful = 0;
        int packagesFailed = 0;
        
        Console.WriteLine($"[客户端 {clientId}] 启动，目标发送间隔: {sendIntervalMs}ms");
        
        while (_isRunning && !cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            NetworkStream? stream = null;
            
            try
            {
                // 连接到服务器
                client = new TcpClient();
                var connectStart = DateTime.UtcNow;
                await client.ConnectAsync(host, port);
                var connectDuration = (DateTime.UtcNow - connectStart).TotalMilliseconds;
                
                stream = client.GetStream();
                Interlocked.Increment(ref _totalConnections);
                
                Console.WriteLine($"[客户端 {clientId}] 已连接到服务器，耗时: {connectDuration:F0}ms");
                
                // 持续发送数据包
                while (_isRunning && !cancellationToken.IsCancellationRequested && client.Connected)
                {
                    try
                    {
                        var packageData = GeneratePackageData();
                        var dataBytes = Encoding.UTF8.GetBytes(packageData);
                        
                        var sendStart = DateTime.UtcNow;
                        await stream.WriteAsync(dataBytes, cancellationToken);
                        await stream.FlushAsync(cancellationToken);
                        var sendDuration = (DateTime.UtcNow - sendStart).TotalMilliseconds;
                        
                        _latencyMeasurements.Enqueue(sendDuration);
                        packagesSent++;
                        packagesSuccessful++;
                        
                        // 更新全局统计
                        lock (_statsLock)
                        {
                            _totalSent++;
                            _totalSuccessful++;
                        }
                        
                        // 限制延迟队列大小
                        while (_latencyMeasurements.Count > 5000)
                        {
                            _latencyMeasurements.TryDequeue(out _);
                        }
                        
                        // 如果发送耗时过长，记录警告
                        if (sendDuration > 50)
                        {
                            Console.WriteLine($"[客户端 {clientId}] ⚠️ 发送耗时异常: {sendDuration:F0}ms");
                        }
                        
                        // 控制发送频率
                        await Task.Delay(sendIntervalMs, cancellationToken);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        packagesFailed++;
                        lock (_statsLock)
                        {
                            _totalSent++;
                            _totalFailed++;
                        }
                        Console.WriteLine($"[客户端 {clientId}] 发送数据失败: {ex.Message}");
                        break; // 退出内层循环，重新连接
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[客户端 {clientId}] 连接失败: {ex.Message}");
                await Task.Delay(1000, cancellationToken); // 等待1秒后重试
            }
            finally
            {
                Interlocked.Increment(ref _totalDisconnections);
                stream?.Dispose();
                client?.Close();
                
                if (_isRunning && !cancellationToken.IsCancellationRequested)
                {
                    Console.WriteLine($"[客户端 {clientId}] 连接断开，准备重连...");
                    await Task.Delay(1000, cancellationToken); // 重连间隔
                }
            }
        }
        
        Console.WriteLine($"[客户端 {clientId}] 停止运行 - 发送: {packagesSent}, 成功: {packagesSuccessful}, 失败: {packagesFailed}");
    }
    
    private static string GeneratePackageData()
    {
        // 生成随机包裹数据，格式: {code},{weight},{length},{width},{height},{volume},{timestamp}@
        var barcode = GenerateRandomBarcode();
        var weight = Random.NextSingle() * 10 + 0.1f; // 0.1-10.1 kg
        var length = Random.NextDouble() * 50 + 10; // 10-60 cm
        var width = Random.NextDouble() * 30 + 10;  // 10-40 cm  
        var height = Random.NextDouble() * 20 + 5;  // 5-25 cm
        var volume = length * width * height / 1000; // 转换为升
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        
        return $"{barcode},{weight:F1},{length:F1},{width:F1},{height:F1},{volume:F2},{timestamp}@";
    }
    
    private static string GenerateRandomBarcode()
    {
        // 生成类似真实快递单号的条码
        var prefixes = new[] { "B", "C", "D", "E", "F", "G", "T", "S" };
        var prefix = prefixes[Random.Next(prefixes.Length)];
        var number = Random.Next(10000000, 99999999);
        var suffix = (char)('A' + Random.Next(0, 26));
        
        return $"{prefix}{number}{suffix}";
    }
    
    private static Task StartAdvancedStatsReporter(TestConfig config)
    {
        return Task.Run(async () =>
        {
            var lastSent = 0;
            var lastTime = DateTime.UtcNow;
            var reportCounter = 0;
            
            while (_isRunning)
            {
                await Task.Delay(3000); // 每3秒报告一次
                
                var currentTime = DateTime.UtcNow;
                var currentSent = _totalSent;
                var currentSuccessful = _totalSuccessful;
                var currentFailed = _totalFailed;
                
                var deltaTime = (currentTime - lastTime).TotalSeconds;
                var deltaSent = currentSent - lastSent;
                var rate = deltaTime > 0 ? deltaSent / deltaTime : 0;
                
                reportCounter++;
                if (reportCounter % 3 == 0) // 每9秒显示详细信息
                {
                    if (config.EnableCoordinatedMode)
                    {
                        var plcSent = _plcSimulator?.TotalSent ?? 0;
                        var plcSuccessful = _plcSimulator?.TotalSuccessful ?? 0;
                        var cameraReceived = _coordinatedCameraSimulator?.TotalReceived ?? 0;
                        var cameraSent = _coordinatedCameraSimulator?.TotalSent ?? 0;
                        
                        Console.WriteLine($"📊 [协调统计] PLC发送: {plcSent}({plcSuccessful}成功) | 相机接收: {cameraReceived} | 相机发送: {cameraSent} | 总速率: {rate:F1} 包/秒");
                    }
                    else
                    {
                        Console.WriteLine($"📊 [详细统计] 速率: {rate:F1} 包/秒 | 总计: {currentSent} | 成功: {currentSuccessful} | 失败: {currentFailed} | 连接: {_totalConnections} | 断开: {_totalDisconnections}");
                    }
                }
                else
                {
                    Console.WriteLine($"📊 [统计] 速率: {rate:F1} 包/秒 | 总计: {currentSent} | 连接状态: {_totalConnections - _totalDisconnections}");
                }
                
                lastSent = currentSent;
                lastTime = currentTime;
            }
        });
    }
    
    private static Task StartLatencyAnalyzer()
    {
        return Task.Run(async () =>
        {
            while (_isRunning)
            {
                await Task.Delay(15000); // 每15秒分析一次延迟
                
                PrintLatencyAnalysis();
            }
        });
    }
    
    private static void PrintLatencyAnalysis()
    {
        if (_latencyMeasurements.IsEmpty) return;
        
        var latencies = new List<double>();
        var tempQueue = new List<double>();
        
        // 收集当前的延迟数据，但保留在队列中
        while (_latencyMeasurements.TryDequeue(out var latency))
        {
            latencies.Add(latency);
            tempQueue.Add(latency);
        }
        
        // 将数据放回队列
        foreach (var latency in tempQueue)
        {
            _latencyMeasurements.Enqueue(latency);
        }
        
        if (latencies.Count > 0)
        {
            latencies.Sort();
            var p50 = latencies[latencies.Count / 2];
            var p90 = latencies[(int)(latencies.Count * 0.90)];
            var p95 = latencies[(int)(latencies.Count * 0.95)];
            var p99 = latencies[(int)(latencies.Count * 0.99)];
            var avg = latencies.Average();
            var max = latencies.Max();
            var min = latencies.Min();
            
            Console.WriteLine($"📈 [延迟分析] 样本: {latencies.Count} | 平均: {avg:F1}ms | P50: {p50:F1}ms | P90: {p90:F1}ms | P95: {p95:F1}ms | P99: {p99:F1}ms | 最小: {min:F1}ms | 最大: {max:F1}ms");
        }
    }
    
    private static void PrintDetailedStats(TestConfig config)
    {
        Console.WriteLine();
        Console.WriteLine("=== 详细统计信息 ===");
        Console.WriteLine($"总发送包裹: {_totalSent}");
        Console.WriteLine($"发送成功: {_totalSuccessful}");
        Console.WriteLine($"发送失败: {_totalFailed}");
        Console.WriteLine($"成功率: {(_totalSent > 0 ? (double)_totalSuccessful / _totalSent * 100 : 0):F2}%");
        
        if (config.EnableCoordinatedMode)
        {
            Console.WriteLine();
            Console.WriteLine("=== 协调模式统计 ===");
            Console.WriteLine($"PLC信号发送: {_plcSimulator?.TotalSent ?? 0}");
            Console.WriteLine($"PLC信号成功: {_plcSimulator?.TotalSuccessful ?? 0}");
            Console.WriteLine($"相机接收信号: {_coordinatedCameraSimulator?.TotalReceived ?? 0}");
            Console.WriteLine($"相机发送数据: {_coordinatedCameraSimulator?.TotalSent ?? 0}");
            Console.WriteLine($"相机发送成功: {_coordinatedCameraSimulator?.TotalSuccessful ?? 0}");
        }
        else
        {
            Console.WriteLine($"总连接数: {_totalConnections}");
            Console.WriteLine($"总断开数: {_totalDisconnections}");
            Console.WriteLine($"当前活跃连接: {_totalConnections - _totalDisconnections}");
        }
        
        Console.WriteLine($"延迟测量样本: {_latencyMeasurements.Count}");
        Console.WriteLine("==================");
        Console.WriteLine();
    }
    
    private static void ResetStats()
    {
        _totalSent = 0;
        _totalSuccessful = 0;
        _totalFailed = 0;
        _totalConnections = 0;
        _totalDisconnections = 0;
        
        // 清空延迟队列
        while (_latencyMeasurements.TryDequeue(out _)) { }
        
        Console.WriteLine("📊 统计数据已重置");
    }
    
    private static void PrintFinalReport(TestConfig config)
    {
        Console.WriteLine();
        Console.WriteLine("🎯 ================ 最终测试报告 ================");
        Console.WriteLine($"测试配置:");
        Console.WriteLine($"  模式: {GetTestModeDescription(config)}");
        
        if (config.EnableCoordinatedMode)
        {
            Console.WriteLine($"  PLC服务器: {config.PlcHost}:{config.PlcPort}");
            Console.WriteLine($"  相机服务器: {config.ServerHost}:{config.ServerPort}");
            Console.WriteLine($"  相机延迟: {config.CameraDelayMin}-{config.CameraDelayMax}ms");
        }
        else
        {
            Console.WriteLine($"  服务器: {config.ServerHost}:{config.ServerPort}");
            Console.WriteLine($"  并发客户端: {config.ConcurrentClients}");
        }
        
        Console.WriteLine($"  目标频率: {config.PackagesPerSecond} 包/秒");
        Console.WriteLine($"  测试时长: {config.TestDurationSeconds} 秒");
        Console.WriteLine();
        Console.WriteLine($"性能结果:");
        Console.WriteLine($"  总发送包裹: {_totalSent:N0}");
        Console.WriteLine($"  发送成功: {_totalSuccessful:N0}");
        Console.WriteLine($"  发送失败: {_totalFailed:N0}");
        Console.WriteLine($"  成功率: {(_totalSent > 0 ? (double)_totalSuccessful / _totalSent * 100 : 0):F2}%");
        Console.WriteLine($"  实际吞吐量: {(_totalSuccessful / (double)config.TestDurationSeconds):F2} 包/秒");
        
        if (config.EnableCoordinatedMode)
        {
            Console.WriteLine();
            Console.WriteLine($"协调模式结果:");
            Console.WriteLine($"  PLC信号发送: {_plcSimulator?.TotalSent ?? 0:N0}");
            Console.WriteLine($"  相机触发次数: {_coordinatedCameraSimulator?.TotalReceived ?? 0:N0}");
            Console.WriteLine($"  信号匹配率: {(_plcSimulator?.TotalSent > 0 ? (double)(_coordinatedCameraSimulator?.TotalReceived ?? 0) / _plcSimulator.TotalSent * 100 : 0):F2}%");
            
            var (avgDelay, minDelay, maxDelay, delayCount) = _coordinatedCameraSimulator?.GetDelayStatistics() ?? (0, 0, 0, 0);
            Console.WriteLine($"  平均相机延迟: {avgDelay:F1}ms");
            Console.WriteLine($"  延迟范围: {minDelay:F1}ms - {maxDelay:F1}ms");
        }
        else
        {
            var expectedThroughput = config.EnableStressMode ? 
                config.ConcurrentClients * config.BurstSize / 2.0 : // 估算突发模式吞吐量
                config.ConcurrentClients * config.PackagesPerSecond;
            Console.WriteLine($"  预期吞吐量: {expectedThroughput:F2} 包/秒");
            Console.WriteLine($"  吞吐量达成率: {(_totalSuccessful / (double)config.TestDurationSeconds) / expectedThroughput * 100:F1}%");
            
            Console.WriteLine();
            Console.WriteLine($"连接统计:");
            Console.WriteLine($"  总连接数: {_totalConnections:N0}");
            Console.WriteLine($"  总断开数: {_totalDisconnections:N0}");
            Console.WriteLine($"  平均连接时长: {(config.TestDurationSeconds / (double)Math.Max(_totalConnections, 1)):F1} 秒");
        }
        
        // 最终延迟分析
        PrintLatencyAnalysis();
        
        Console.WriteLine("===============================================");
    }
}

public class TestConfig
{
    public string ServerHost { get; set; } = "127.0.0.1";
    public int ServerPort { get; set; } = 20011;
    public string PlcHost { get; set; } = "127.0.0.1";
    public int PlcPort { get; set; } = 20010;
    public int ConcurrentClients { get; set; } = 3;
    public int PackagesPerSecond { get; set; } = 10;
    public int TestDurationSeconds { get; set; } = 60;
    public bool EnableStressMode { get; set; } = false;
    public int BurstSize { get; set; } = 20;
    
    // 新增：协调模式配置
    public bool EnableCoordinatedMode { get; set; } = false;
    public int CameraDelayMin { get; set; } = 800;
    public int CameraDelayMax { get; set; } = 900;
} 