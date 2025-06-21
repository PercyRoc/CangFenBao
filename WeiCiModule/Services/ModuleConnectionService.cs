using System.Net;
using System.Net.Sockets;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Channels;
using Serilog;

namespace WeiCiModule.Services;

/// <summary>
/// 模组带连接服务实现 (基于响应式流重构 + 三层隔离架构)
/// </summary>
internal class ModuleConnectionService : IModuleConnectionService, IDisposable
{
    // 数据包相关常量
    private const byte StartCode = 0xF9; // 起始码 16#F9
    private const byte FunctionCodeReceive = 0x10; // 接收包裹序号的功能码 16#10
    private const byte FunctionCodeSend = 0x11; // 发送分拣指令的功能码 16#11
    private const byte FunctionCodeFeedback = 0x12; // 反馈指令的功能码 16#12
    private const int PackageLength = 8; // 数据包长度
    private const byte Checksum = 0xFF; // 固定校验位 16#FF
    

    
    // 【三层隔离架构】Subject和发布管道
    private readonly Subject<Timestamped<ushort>> _triggerSignalSubject = new();
    private readonly Channel<Timestamped<ushort>> _publishChannel;
    private readonly ChannelWriter<Timestamped<ushort>> _publishWriter;
    private readonly ChannelReader<Timestamped<ushort>> _publishReader;
    
    // 专用发布线程
    private Thread? _publishThread;
    private volatile bool _publishThreadRunning;

    private TcpClient? _connectedClient;
    private bool _isRunning;
    private NetworkStream? _networkStream;
    private CancellationTokenSource? _receiveCts;
    private TcpListener? _tcpListener;

    public ModuleConnectionService()
    {
        TriggerSignalStream = _triggerSignalSubject.AsObservable();
        
        // 【三层隔离架构】初始化发布Channel
        var options = new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };
        _publishChannel = Channel.CreateBounded<Timestamped<ushort>>(options);
        _publishWriter = _publishChannel.Writer;
        _publishReader = _publishChannel.Reader;
        
        // 启动专用发布线程
        StartPublishThread();
    }
    
    // 【核心修改】属性类型改变
    public IObservable<Timestamped<ushort>> TriggerSignalStream { get; }
    
    /// <summary>
    /// 启动专用发布线程
    /// </summary>
    private void StartPublishThread()
    {
        if (_publishThread != null)
        {
            Log.Warning("PLC信号发布线程已存在");
            return;
        }

        _publishThreadRunning = true;
        _publishThread = new Thread(ProcessPublishQueueSync)
        {
            Name = "PLCSignalPublishThread",
            IsBackground = true
        };
        _publishThread.Start();
        Log.Information("PLC信号发布线程已启动: {ThreadName}", _publishThread.Name);
    }

    /// <summary>
    /// 专用发布线程同步处理方法（避免async/await开销）
    /// </summary>
    private void ProcessPublishQueueSync()
    {
        Log.Information("🔧 [PLC发布线程] 开始运行");
        
        try
        {
            while (_publishThreadRunning)
            {
                try
                {
                    // 使用同步方式等待数据，避免async/await线程切换开销
                    if (_publishReader.WaitToReadAsync().AsTask().Wait(100))
                    {
                        while (_publishReader.TryRead(out var signal))
                        {
                            try
                            {
                                var publishStart = DateTimeOffset.UtcNow;
                                var publishDelay = (publishStart - signal.Timestamp).TotalMilliseconds;
                                
                                Log.Debug("⏱️  [PLC发布线程] 信号={Signal}, 创建时间={CreateTime}, 发布延迟={Delay:F0}ms", 
                                    signal.Value, signal.Timestamp.ToString("HH:mm:ss.fff"), publishDelay);
                                
                                // 发布到Subject（这是唯一调用OnNext的地方）
                                var subjectStart = DateTimeOffset.UtcNow;
                                _triggerSignalSubject.OnNext(signal);
                                var subjectDuration = (DateTimeOffset.UtcNow - subjectStart).TotalMilliseconds;
                                
                                Log.Debug("PLC信号成功发布: 序号={Signal}, Subject发布耗时={Duration:F0}ms", 
                                    signal.Value, subjectDuration);
                                
                                // 监控Subject发布性能
                                if (subjectDuration > 50)
                                {
                                    Log.Warning("⚠️  PLC Subject.OnNext耗时异常: {Duration:F0}ms, 信号={Signal}", 
                                        subjectDuration, signal.Value);
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, "❌ PLC信号发布失败: 序号={Signal}", signal.Value);
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not ThreadAbortException)
                {
                    Log.Error(ex, "🔧 [PLC发布线程] 处理发布队列时发生错误");
                    Thread.Sleep(1000); // 发生错误时等待1秒
                }
            }
        }
        catch (ThreadAbortException)
        {
            Log.Information("🔧 [PLC发布线程] 收到线程中止信号");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "🔧 [PLC发布线程] 发生未预期错误");
        }
        finally
        {
            Log.Information("🔧 [PLC发布线程] 停止运行");
        }
    }

    /// <summary>
    /// 停止专用发布线程
    /// </summary>
    private void StopPublishThread()
    {
        if (_publishThread == null) return;

        try
        {
            Log.Information("正在停止PLC信号发布线程...");
            
            _publishThreadRunning = false;
            _publishWriter.TryComplete(); // 关闭Channel写入端
            
            // 等待线程正常退出，最多等待3秒
            if (!_publishThread.Join(3000))
            {
                Log.Warning("PLC信号发布线程未在3秒内正常退出，将等待其自然结束");
                // .NET Core/5+ 不支持 Thread.Abort()，依赖 _publishThreadRunning 标志自然退出
            }
            
            _publishThread = null;
            Log.Information("PLC信号发布线程已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止PLC信号发布线程时发生错误");
        }
    }

    public bool IsConnected => _connectedClient?.Connected ?? false;

    public event EventHandler<bool>? ConnectionStateChanged;

    public Task<bool> StartServerAsync(string ipAddress, int port)
    {
        try
        {
            if (_isRunning)
            {
                Log.Warning("服务器已经在运行中");
                return Task.FromResult(false);
            }

            Log.Information("正在尝试启动TCP服务器...");
            Log.Information("绑定地址: {IpAddress}, 端口: {Port}", ipAddress, port);

            IPAddress ip;
            try
            {
                ip = IPAddress.Parse(ipAddress);
                Log.Information("IP地址解析结果: {ParsedIp}", ip);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "IP地址解析失败: {IpAddress}", ipAddress);
                return Task.FromResult(false);
            }

            _tcpListener = new TcpListener(ip, port);

            try
            {
                _tcpListener.Start();
                _isRunning = true;
                Log.Information("TCP服务器启动成功，正在监听: {IpAddress}:{Port}", ipAddress, port);

                // 开始异步等待客户端连接
                _ = AcceptClientAsync();
                return Task.FromResult(true);
            }
            catch (SocketException ex)
            {
                Log.Error(ex, "TCP服务器启动失败 - Socket错误代码: {ErrorCode}, 消息: {Message}", ex.ErrorCode, ex.Message);
                return Task.FromResult(false);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动TCP服务器时发生未知错误");
            return Task.FromResult(false);
        }
    }

    public Task StopServerAsync()
    {
        try
        {
            Log.Information("正在停止模组带TCP服务器...");
            
            if (!_isRunning) 
            {
                Log.Debug("服务器已经停止");
                return Task.CompletedTask;
            }

            _isRunning = false;
            
            // 停止监听器
            if (_tcpListener != null)
            {
                _tcpListener.Stop();
                Log.Debug("TCP监听器已停止");
            }
            
            // 停止接收数据
            if (_receiveCts != null)
            {
                _receiveCts.Cancel();
                _receiveCts.Dispose();
                _receiveCts = null;
                Log.Debug("数据接收已停止");
            }
            
            // 关闭客户端连接
            if (_connectedClient != null)
            {
                if (_connectedClient.Connected)
                {
                    _connectedClient.Close();
                }
                _connectedClient = null;
                OnConnectionStateChanged(false);
                Log.Debug("客户端连接已关闭");
            }
            
            // 关闭网络流
            if (_networkStream != null)
            {
                _networkStream.Dispose();
                _networkStream = null;
                Log.Debug("网络流已释放");
            }
            
            // 停止专用发布线程
            StopPublishThread();
            
            // 完成信号流
            try
            {
                _triggerSignalSubject.OnCompleted();
                Log.Debug("信号流已完成");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "完成信号流时发生错误");
            }

            Log.Information("模组带TCP服务器已完全停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止TCP服务器时发生错误");
            throw;
        }

        return Task.CompletedTask;
    }

    public async Task SendSortingCommandAsync(ushort packageNumber, byte chute)
    {
        if (_networkStream == null || _connectedClient?.Connected != true)
        {
            Log.Warning("无法发送分拣指令：未连接到模组带控制器。");
            return;
        }

        try
        {
            // 构建分拣指令
            var command = new byte[PackageLength];
            command[0] = StartCode; // 起始码
            command[1] = FunctionCodeSend; // 功能码
            command[2] = (byte)(packageNumber >> 8 & 0xFF); // 包裹序号高字节
            command[3] = (byte)(packageNumber & 0xFF); // 包裹序号低字节
            command[4] = 0x00; // 预留
            command[5] = 0x00; // 预留
            command[6] = chute; // 格口号
            command[7] = Checksum; // 校验和

            await _networkStream.WriteAsync(command);
            await _networkStream.FlushAsync();

            Log.Debug("发送分拣指令: {Command}", BitConverter.ToString(command));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "发送分拣指令失败: PackageNumber={PackageNumber}, Chute={Chute}",
                packageNumber, chute);
            // 可以在此触发重连或通知上层
        }
    }

    private async Task AcceptClientAsync()
    {
        try
        {
            while (_isRunning)
            {
                try
                {
                    Log.Information("等待客户端连接...");
                    _connectedClient = await _tcpListener?.AcceptTcpClientAsync()!;
                    _networkStream = _connectedClient.GetStream();
                    OnConnectionStateChanged(true);
                    Log.Information("客户端已连接");

                    // 开始接收数据
                    StartReceiving();
                }
                catch (ObjectDisposedException)
                {
                    // TCP监听器已被释放，这是正常的关闭流程
                    Log.Debug("TCP监听器已被释放，停止接受连接");
                    break;
                }
                catch (SocketException ex) when (ex.ErrorCode == 995) // WSA_OPERATION_ABORTED
                {
                    // 操作被中止，这是正常的关闭流程
                    Log.Debug("TCP监听操作被中止，停止接受连接");
                    break;
                }
                catch (Exception ex)
                {
                    if (_isRunning) 
                    {
                        Log.Error(ex, "接受客户端连接时发生错误");
                        // 等待一段时间后重试
                        await Task.Delay(1000);
                    }
                    else
                    {
                        Log.Debug("服务已停止，退出连接接受循环");
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AcceptClientAsync方法发生未预期的错误");
        }
        finally
        {
            Log.Debug("AcceptClientAsync任务已结束");
        }
    }

    private void StartReceiving()
    {
        _receiveCts?.Cancel();
        _receiveCts?.Dispose();
        _receiveCts = new CancellationTokenSource();

        _ = Task.Run(async () =>
        {
            var buffer = new byte[1024];
            var packageBuffer = new byte[PackageLength];
            var packageIndex = 0;

            while (!_receiveCts.Token.IsCancellationRequested)
                try
                {
                    if (_networkStream == null)
                    {
                        await Task.Delay(1000, _receiveCts.Token);
                        continue;
                    }

                    var bytesRead = await _networkStream.ReadAsync(buffer);
                    if (bytesRead == 0)
                    {
                        Log.Warning("模组带控制器连接已断开");
                        await DisconnectClientAsync();
                        continue;
                    }

                    for (var i = 0; i < bytesRead; i++)
                        if (packageIndex == 0)
                        {
                            // 检查起始码
                            if (buffer[i] == StartCode)
                            {
                                packageBuffer[packageIndex++] = buffer[i];
                            }
                        }
                        else
                        {
                            packageBuffer[packageIndex++] = buffer[i];

                            if (packageIndex != PackageLength) continue;
                            // 【关键修复】改为同步处理，消除异步调用开销和线程切换延迟
                            ProcessPackageData(packageBuffer);
                            packageIndex = 0;
                        }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "接收模组带数据异常");
                    await Task.Delay(1000, _receiveCts.Token);
                }
        }, _receiveCts.Token);
    }

    private void ProcessPackageData(byte[] data)
    {
        try
        {
            // 【性能优化】立即记录接收时间戳，避免后续处理影响时间精度
            var receiveTime = DateTimeOffset.UtcNow;
            
            // 验证数据包格式
            if (!ValidatePackage(data))
            {
                Log.Warning("数据包验证失败: {Data}", BitConverter.ToString(data));
                return;
            }

            // 根据功能码处理不同类型的数据包
            switch (data[1])
            {
                case FunctionCodeReceive:
                    // 处理包裹序号数据包（PLC -> PC）
                    ProcessPackageNumber(data, receiveTime);
                    break;

                case FunctionCodeFeedback:
                    // 处理反馈指令数据包（PLC -> PC 确认）
                    ProcessFeedback(data);
                    break;

                default:
                    Log.Warning("未知的功能码: 0x{FunctionCode:X2}", data[1]);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理模组带数据包异常: {Data}", BitConverter.ToString(data));
        }
    }

    private void ProcessPackageNumber(byte[] data, DateTimeOffset receiveTime)
    {
        try
        {
            // 解析包裹序号
            var packageNumber = (ushort)(data[2] << 8 | data[3]);
            Log.Information("收到包裹触发信号: 序号={PackageNumber}", packageNumber);
            
            // 【三层隔离架构】使用数据包到达时的时间戳，加入发布队列而不是直接发布
            var timestampedSignal = new Timestamped<ushort>(packageNumber, receiveTime);
            
            // 将信号加入发布队列，由专用线程异步发布到Subject
            var enqueueStart = DateTimeOffset.UtcNow;
            if (_publishWriter.TryWrite(timestampedSignal))
            {
                var enqueueDuration = (DateTimeOffset.UtcNow - enqueueStart).TotalMilliseconds;
                Log.Debug("PLC信号已成功加入发布队列: 序号={PackageNumber}, 入队耗时={Duration:F0}ms", 
                    packageNumber, enqueueDuration);
                
                if (enqueueDuration > 10)
                {
                    Log.Warning("⚠️  PLC信号入队耗时异常: {Duration:F0}ms, 序号={PackageNumber}", 
                        enqueueDuration, packageNumber);
                }
            }
            else
            {
                Log.Error("❌ PLC信号发布队列已满，丢弃信号: 序号={PackageNumber}", packageNumber);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹序号数据包异常: {Data}", BitConverter.ToString(data));
        }
    }
    
    private static void ProcessFeedback(byte[] data)
    {
        try
        {
            // 解析包裹序号
            var packageNumber = (ushort)((data[2] << 8) + data[3]);
            var errorCode = data[5]; // 异常码
            var chute = data[6]; // 格口号

            Log.Information("收到分拣反馈: 包裹序号={PackageNumber}, 异常码=0x{ErrorCode:X2}, 格口={Chute}",
                packageNumber, errorCode, chute);

            // 检查异常码
            if (errorCode != 0)
                Log.Warning("分拣异常: 包裹序号={PackageNumber}, 异常码=0x{ErrorCode:X2}",
                    packageNumber, errorCode);

            // 此处可以添加一个 Subject<FeedbackInfo> 来通知上层反馈结果，如果需要的话
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理反馈指令异常: {Data}", BitConverter.ToString(data));
        }
    }
    
    private void OnConnectionStateChanged(bool isConnected)
    {
        ConnectionStateChanged?.Invoke(this, isConnected);
    }

    private async Task DisconnectClientAsync()
    {
        try
        {
            _receiveCts?.Cancel();
            _receiveCts?.Dispose();
            _receiveCts = null;

            if (_networkStream != null)
            {
                await _networkStream.DisposeAsync();
                _networkStream = null;
            }

            if (_connectedClient != null)
            {
                _connectedClient.Close();
                _connectedClient = null;
                OnConnectionStateChanged(false);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "断开客户端连接时发生错误");
        }
    }

    private static bool ValidatePackage(byte[] data)
    {
        // 检查数据包长度
        if (data.Length != PackageLength)
        {
            Log.Warning("数据包长度错误: 期望={Expected}, 实际={Actual}", PackageLength, data.Length);
            return false;
        }

        // 检查起始码
        if (data[0] != StartCode)
        {
            Log.Warning("数据包起始码错误: 期望=0x{Expected:X2}, 实际=0x{Actual:X2}", StartCode, data[0]);
            return false;
        }

        // 检查校验和
        if (data[^1] == Checksum) return true;

        Log.Warning("数据包校验和错误: 期望=0x{Expected:X2}, 实际=0x{Actual:X2}", Checksum, data[^1]);
        return false;
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        try
        {
            Log.Information("正在释放ModuleConnectionService资源...");
            
            // 停止服务器
            if (_isRunning)
            {
                StopServerAsync().Wait(TimeSpan.FromSeconds(5));
            }
            
            // 停止发布线程
            StopPublishThread();
            
            // 释放Subject
            try
            {
                _triggerSignalSubject?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "释放Subject时发生错误");
            }
            
            Log.Information("ModuleConnectionService资源已释放");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "释放ModuleConnectionService资源时发生错误");
        }
        
        GC.SuppressFinalize(this);
    }
} 