using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace TcpCameraSimulator;

/// <summary>
/// 模组带PLC模拟器 - 发送包裹序号信号
/// </summary>
public class PlcSimulator : IDisposable
{
    // PLC协议常量
    private const byte StartCode = 0xF9; // 起始码 16#F9
    private const byte FunctionCodeReceive = 0x10; // 接收包裹序号的功能码 16#10
    private const int PackageLength = 8; // 数据包长度
    private const byte Checksum = 0xFF; // 固定校验位 16#FF
    
    private readonly string _host;
    private readonly int _port;
    private readonly Random _random = new();
    private volatile bool _isRunning = true;
    
    // 包裹序号管理
    private ushort _currentPackageNumber = 1;
    private readonly object _packageNumberLock = new();
    
    // 统计信息
    private volatile int _totalSent = 0;
    private volatile int _totalSuccessful = 0;
    private volatile int _totalFailed = 0;
    
    // 与相机模拟器的协调
    private readonly ConcurrentQueue<PlcSignal> _signalQueue = new();
    
    public PlcSimulator(string host = "127.0.0.1", int port = 20010)
    {
        _host = host;
        _port = port;
    }
    
    public event EventHandler<PlcSignal>? SignalSent;
    
    public int TotalSent => _totalSent;
    public int TotalSuccessful => _totalSuccessful;
    public int TotalFailed => _totalFailed;
    
    /// <summary>
    /// 启动PLC模拟器
    /// </summary>
    public async Task StartAsync(int packagesPerSecond, CancellationToken cancellationToken)
    {
        int sendIntervalMs = 1000 / packagesPerSecond;
        
        Log.Information("🔧 [PLC模拟器] 启动，目标服务器: {Host}:{Port}, 发送间隔: {Interval}ms", 
            _host, _port, sendIntervalMs);
        
        while (_isRunning && !cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            NetworkStream? stream = null;
            
            try
            {
                // 连接到PLC服务器
                client = new TcpClient();
                var connectStart = DateTime.UtcNow;
                await client.ConnectAsync(_host, _port);
                var connectDuration = (DateTime.UtcNow - connectStart).TotalMilliseconds;
                
                stream = client.GetStream();
                Log.Information("🔧 [PLC模拟器] 连接成功，耗时: {Duration:F0}ms", connectDuration);
                
                // 持续发送PLC信号
                while (_isRunning && !cancellationToken.IsCancellationRequested && client.Connected)
                {
                    try
                    {
                        var packageNumber = GetNextPackageNumber();
                        var signal = new PlcSignal
                        {
                            PackageNumber = packageNumber,
                            Timestamp = DateTimeOffset.UtcNow,
                            Barcode = GenerateBarcode(packageNumber)
                        };
                        
                        var command = BuildPlcCommand(packageNumber);
                        
                        var sendStart = DateTime.UtcNow;
                        await stream.WriteAsync(command, cancellationToken);
                        await stream.FlushAsync(cancellationToken);
                        var sendDuration = (DateTime.UtcNow - sendStart).TotalMilliseconds;
                        
                        Interlocked.Increment(ref _totalSent);
                        Interlocked.Increment(ref _totalSuccessful);
                        
                        // 触发事件，通知相机模拟器
                        SignalSent?.Invoke(this, signal);
                        _signalQueue.Enqueue(signal);
                        
                        Log.Information("🔧 [PLC模拟器] 发送信号: 序号={PackageNumber}, 条码={Barcode}, 耗时={Duration:F0}ms", 
                            packageNumber, signal.Barcode, sendDuration);
                        
                        if (sendDuration > 50)
                        {
                            Log.Warning("🔧 [PLC模拟器] ⚠️ 发送延迟高: {Duration:F0}ms", sendDuration);
                        }
                        
                        // 控制发送频率
                        await Task.Delay(sendIntervalMs, cancellationToken);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        Interlocked.Increment(ref _totalSent);
                        Interlocked.Increment(ref _totalFailed);
                        Log.Error(ex, "🔧 [PLC模拟器] 发送信号失败");
                        break; // 退出内层循环，重新连接
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "🔧 [PLC模拟器] 连接失败");
                await Task.Delay(2000, cancellationToken); // 等待2秒后重试
            }
            finally
            {
                stream?.Dispose();
                client?.Close();
                
                if (_isRunning && !cancellationToken.IsCancellationRequested)
                {
                    Log.Information("🔧 [PLC模拟器] 连接断开，准备重连...");
                    await Task.Delay(1000, cancellationToken);
                }
            }
        }
        
        Log.Information("🔧 [PLC模拟器] 停止运行");
    }
    
    /// <summary>
    /// 停止PLC模拟器
    /// </summary>
    public void Stop()
    {
        _isRunning = false;
        Log.Information("🔧 [PLC模拟器] 收到停止信号");
    }
    
    /// <summary>
    /// 获取下一个包裹序号
    /// </summary>
    private ushort GetNextPackageNumber()
    {
        lock (_packageNumberLock)
        {
            var current = _currentPackageNumber;
            _currentPackageNumber++;
            
            // 避免序号溢出，从1重新开始
            if (_currentPackageNumber == 0)
            {
                _currentPackageNumber = 1;
            }
            
            return current;
        }
    }
    
    /// <summary>
    /// 构建PLC指令数据包
    /// </summary>
    private byte[] BuildPlcCommand(ushort packageNumber)
    {
        var command = new byte[PackageLength];
        command[0] = StartCode; // 起始码
        command[1] = FunctionCodeReceive; // 功能码
        command[2] = (byte)(packageNumber >> 8 & 0xFF); // 包裹序号高字节
        command[3] = (byte)(packageNumber & 0xFF); // 包裹序号低字节
        command[4] = 0x00; // 预留
        command[5] = 0x00; // 预留
        command[6] = 0x00; // 预留
        command[7] = Checksum; // 校验和
        
        return command;
    }
    
    /// <summary>
    /// 为包裹序号生成对应的条码
    /// </summary>
    private string GenerateBarcode(ushort packageNumber)
    {
        // 生成与包裹序号相关的条码，确保可追踪
        var prefixes = new[] { "P", "M", "L", "K" }; // PLC专用前缀
        var prefix = prefixes[packageNumber % prefixes.Length];
        var baseNumber = packageNumber + 10000000; // 确保8位数字
        var suffix = (char)('A' + (packageNumber % 26));
        
        return $"{prefix}{baseNumber:D8}{suffix}";
    }
    
    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// PLC信号数据结构
/// </summary>
public class PlcSignal
{
    public ushort PackageNumber { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string Barcode { get; set; } = string.Empty;
} 