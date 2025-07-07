using System.Reactive;
using System.Reactive.Subjects;
using System.Text;
using Common.Models.Package;
using Serilog;
using System.Net.Sockets;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Channels;

namespace Camera.Services.Implementations.TCP;

/// <summary>
///     TCP相机服务实现 (客户端模式：主动连接相机设备)
/// </summary>
public class TcpCameraService : IDisposable
{
    private const int MaxBufferSize = 1024 * 1024; // 最大缓冲区大小（1MB）

    private readonly Subject<Timestamped<PackageInfo>> _packageTimestampedSubject = new();
    
    // 【核心改进】使用高性能Channel代替BlockingCollection
    private readonly Channel<(byte[] data, DateTimeOffset timestamp)> _dataChannel;
    private readonly ChannelWriter<(byte[] data, DateTimeOffset timestamp)> _dataWriter;
    private readonly ChannelReader<(byte[] data, DateTimeOffset timestamp)> _dataReader;
    
    // 【终极修复】新增独立的包裹发布队列和线程，彻底隔离Subject背压
    private readonly Channel<Timestamped<PackageInfo>> _publishChannel;
    private readonly ChannelWriter<Timestamped<PackageInfo>> _publishWriter;
    private readonly ChannelReader<Timestamped<PackageInfo>> _publishReader;
    
    private readonly Task _processingTask;
    private readonly Task _publishingTask; // 新增：专用发布线程
    private Task? _connectionTask; // 新增：连接管理任务
    private readonly CancellationTokenSource _cts;

    private readonly int _port;
    private readonly string _host;
    
    // 【客户端模式】TCP客户端和连接管理
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly StringBuilder _receiveBuffer = new(); // 接收缓冲区

    /// <summary>
    ///     构造函数：初始化TCP相机服务（客户端模式）
    /// </summary>
    /// <param name="host">相机设备地址</param>
    /// <param name="port">相机设备端口</param>
    public TcpCameraService(string host = "127.0.0.1", int port = 20011)
    {
        _host = host;
        _port = port;
        _cts = new CancellationTokenSource();

        // 【核心改进】创建高性能Channel (无界)
        var channelOptions = new UnboundedChannelOptions
        {
            SingleReader = true,  // 只有一个消费者
            SingleWriter = false, // 可能有多个TCP客户端写入
            AllowSynchronousContinuations = true // 提高性能
        };
        _dataChannel = Channel.CreateUnbounded<(byte[] data, DateTimeOffset timestamp)>(channelOptions);
        _dataWriter = _dataChannel.Writer;
        _dataReader = _dataChannel.Reader;

        // 【终极修复】创建独立的包裹发布Channel，彻底隔离Subject背压影响
        var publishChannelOptions = new UnboundedChannelOptions
        {
            SingleReader = true,  // 只有一个发布线程
            SingleWriter = true,  // 只有数据处理线程写入
            AllowSynchronousContinuations = true
        };
        _publishChannel = Channel.CreateUnbounded<Timestamped<PackageInfo>>(publishChannelOptions);
        _publishWriter = _publishChannel.Writer;
        _publishReader = _publishChannel.Reader;

        // 【终极修复】创建真正的专用线程，避免async/await导致的线程池切换
        _processingTask = new Task(() =>
        {
            var thread = Thread.CurrentThread;
            thread.Name = "TcpCameraDataProcessor";
            // 对于数据入口，使用最高优先级，确保它能抢占其他非关键线程
            thread.Priority = ThreadPriority.Highest;
            
            // 【线程诊断】验证专用线程是否正确创建
            Log.Information("🚀 [专用线程启动] ID={ThreadId}, 名称='{ThreadName}', 是否线程池线程={IsThreadPoolThread}, 优先级={Priority}",
                thread.ManagedThreadId, thread.Name, thread.IsThreadPoolThread, thread.Priority);
            
            if (thread.IsThreadPoolThread)
            {
                Log.Error("🚨 [严重错误] 专用线程创建失败！仍在使用线程池线程，这会导致性能问题！");
            }
            else
            {
                Log.Information("✅ [专用线程] 成功创建独立线程，脱离线程池");
            }
            
            // 【关键修复】使用同步方法，确保始终在专用线程上执行
            ProcessDataQueueSync(_cts.Token);
        }, _cts.Token, TaskCreationOptions.LongRunning);

        // 【终极修复】创建独立的包裹发布线程，彻底隔离Subject背压影响
        _publishingTask = new Task(() =>
        {
            var thread = Thread.CurrentThread;
            thread.Name = "TcpCameraPackagePublisher";
            thread.Priority = ThreadPriority.Normal; // 发布线程使用普通优先级
            
            Log.Information("🚀 [发布线程启动] ID={ThreadId}, 名称='{ThreadName}', 是否线程池线程={IsThreadPoolThread}, 优先级={Priority}",
                thread.ManagedThreadId, thread.Name, thread.IsThreadPoolThread, thread.Priority);
            
            ProcessPublishQueueSync(_cts.Token);
        }, _cts.Token, TaskCreationOptions.LongRunning);

        // 启动专用线程
        _processingTask.Start();
        _publishingTask.Start();
        
        Log.Information("📋 [TcpCameraService] 专用数据处理任务已启动，TaskCreationOptions.LongRunning={LongRunning}", 
            _processingTask.CreationOptions.HasFlag(TaskCreationOptions.LongRunning));
        
        Log.Information("📋 [TcpCameraService] 专用包裹发布任务已启动");

        // 数据处理管道预热
        try
        {
            var testData = Encoding.UTF8.GetBytes("WARMUP_DATA");
            var warmupTimestamp = DateTimeOffset.UtcNow;
            
            if (_dataWriter.TryWrite((testData, warmupTimestamp)))
            {
                Log.Debug("数据处理管道预热完成");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "数据处理管道预热失败");
        }
    }

    public IObservable<Timestamped<PackageInfo>> PackageStream => _packageTimestampedSubject.AsObservable();

    public event Action<string, bool>? ConnectionChanged;

    public void Dispose()
    {
        Log.Debug("正在 Dispose TCP相机服务 (TcpCameraService)...");
        
        // 1. 先取消令牌，这会影响所有使用该令牌的操作
        _cts.Cancel();
        
        // 2. 停止监听器和关闭连接
        Stop();
        
        // 3. 确保数据队列已停止（Stop方法中可能已经调用过）
        try
        {
            _dataWriter.Complete();
            Log.Debug("数据队列已完成（Dispose阶段）");
        }
        catch (InvalidOperationException)
        {
            // 数据队列已经完成，这是正常情况
            Log.Debug("数据队列已经完成");
        }
        
        // 4. 【新增】停止发布队列
        try
        {
            _publishWriter.Complete();
            Log.Debug("发布队列已完成（Dispose阶段）");
        }
        catch (InvalidOperationException)
        {
            Log.Debug("发布队列已经完成");
        }
        
        // 5. 等待所有任务完成，使用更aggressive的方法
        var allTasks = new List<Task>();
        
        // 添加处理任务
        allTasks.Add(_processingTask);

        // 【新增】添加发布任务
        allTasks.Add(_publishingTask);

        // 添加连接任务
        if (_connectionTask != null)
        {
            allTasks.Add(_connectionTask);
        }
        
        try
        {
            // 等待所有任务完成，最多等待3秒
            if (allTasks.Count > 0)
            {
                var waitResult = Task.WaitAll([.. allTasks], TimeSpan.FromSeconds(3));
                
                if (!waitResult)
                {
                    Log.Warning("部分任务未在3秒内完成，将强制结束");
                    
                    // 对于仍在运行的任务，记录详细警告
                    foreach (var task in allTasks.Where(t => !t.IsCompleted))
                    {
                        var taskType = task == _processingTask ? "专用数据处理线程" :
                                      task == _publishingTask ? "专用发布线程" :
                                      task == _connectionTask ? "TCP连接管理线程" : "客户端处理线程";
                        Log.Warning("{TaskType} 状态: {TaskStatus}", taskType, task.Status);
                    }
                    
                    // 对专用线程未停止的情况给出特别警告
                    if (!_processingTask.IsCompleted)
                    {
                        Log.Error("专用数据处理线程未能正常停止，可能存在阻塞问题");
                    }
                    
                    if (_publishingTask != null && !_publishingTask.IsCompleted)
                    {
                        Log.Error("专用发布线程未能正常停止，可能存在阻塞问题");
                    }
                }
                else
                {
                    Log.Debug("所有任务已完成，包括专用数据处理线程和发布线程");
                }
            }
        }
        catch (AggregateException ex)
        {
            Log.Warning(ex, "等待任务完成时发生异常");
        }
        
        // 6. 强制清理资源
        try
        {
            _packageTimestampedSubject?.OnCompleted();
            _packageTimestampedSubject?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "释放Subject时发生错误");
        }
        
        // Channel的Writer不需要手动Dispose
        Log.Debug("数据队列和发布队列已完成");
        
        try
        {
            _cts?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "释放CancellationTokenSource时发生错误");
        }
        
        // 7. 最后的清理
        Log.Debug("TCP相机服务 (TcpCameraService) Dispose 完成");
        GC.SuppressFinalize(this);
    }

    public bool Start()
    {
        try
        {
            Log.Information("正在启动 TCP相机服务 (客户端模式)...");
            Log.Information("目标相机设备: {Host}:{Port}", _host, _port);
            
            _connectionTask = ManageConnectionAsync(_cts.Token);
            Log.Information("TCP相机客户端连接管理器已启动");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动 TCP相机服务失败");
            return false;
        }
    }

    public void Stop()
    {
        try
        {
            Log.Information("正在停止 TCP相机服务...");
            
            // 1. 只有在未取消时才取消令牌
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
                Log.Debug("已发送取消令牌");
            }
            
            // 2. 【关键增强】停止数据队列，确保专用线程能立即退出
            try
            {
                _dataWriter.Complete();
                Log.Debug("数据队列已停止，专用处理线程将退出");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "停止数据队列时发生错误");
            }
            
            // 3. 【新增】停止发布队列
            try
            {
                _publishWriter.Complete();
                Log.Debug("发布队列已停止，专用发布线程将退出");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "停止发布队列时发生错误");
            }
            
            // 4. 断开客户端连接
            try
            {
                _stream?.Dispose();
                _client?.Close();
                Log.Debug("TCP客户端连接已关闭");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "关闭TCP客户端连接时发生错误");
            }
            
            Log.Information("TCP相机服务已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止 TCP相机服务时发生错误");
        }
    }

    /// <summary>
    /// 【新增】连接管理器：负责连接到相机设备并处理重连
    /// </summary>
    private async Task ManageConnectionAsync(CancellationToken cancellationToken)
    {
        Log.Information("📸 [连接管理器] 启动，目标设备: {Host}:{Port}", _host, _port);
        
        var retryCount = 0;
        var maxRetryDelay = 30000; // 最大重试间隔30秒
        var baseRetryDelay = 1000; // 基础重试间隔1秒
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // 如果客户端为空或未连接，则尝试连接
                if (_client == null || !_client.Connected)
                {
                    // 清理旧的资源
                    _stream?.Dispose();
                    _client?.Dispose();
                    
                    if (retryCount == 0)
                    {
                        Log.Information("📸 [连接管理器] 正在连接到相机设备...");
                    }
                    else
                    {
                        Log.Information("📸 [连接管理器] 重试连接到相机设备... (第{RetryCount}次重试)", retryCount);
                    }

                    _client = new TcpClient
                    {
                        // 设置TCP选项
                        NoDelay = true,
                        ReceiveBufferSize = 8192,
                        SendBufferSize = 8192
                    };

                    // 设置连接超时为5秒
                    using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    connectCts.CancelAfter(5000);
                    
                    await _client.ConnectAsync(_host, _port, connectCts.Token);
                    _stream = _client.GetStream();
                    
                    Log.Information("📸 [连接管理器] ✅ 连接相机设备成功！");
                    retryCount = 0; // 连接成功后重置重试计数
                    
                    // 启动数据接收任务
                    _ = ReceiveDataAsync(cancellationToken);
                }
                
                // 连接稳定，每秒检查一次连接状态
                await Task.Delay(1000, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Log.Debug("📸 [连接管理器] 连接任务被取消");
                break;
            }
            catch (OperationCanceledException)
            {
                // 连接超时，继续重试
                retryCount++;
                Log.Warning("📸 [连接管理器] ⏰ 连接超时 (第{RetryCount}次重试)", retryCount);
            }
            catch (SocketException ex)
            {
                retryCount++;
                
                // 根据错误类型提供更友好的提示
                string errorMessage = ex.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused => "连接被拒绝，相机设备可能未启动",
                    SocketError.HostUnreachable => "无法到达主机，请检查网络连接",
                    SocketError.NetworkUnreachable => "网络不可达，请检查网络配置",
                    SocketError.TimedOut => "连接超时",
                    _ => $"网络错误: {ex.SocketErrorCode}"
                };
                
                if (retryCount <= 3)
                {
                    Log.Warning("📸 [连接管理器] ❌ 连接失败: {ErrorMessage} (第{RetryCount}次重试)", errorMessage, retryCount);
                }
                else if (retryCount % 10 == 0) // 每10次重试记录一次，避免日志过多
                {
                    Log.Warning("📸 [连接管理器] ❌ 连接失败: {ErrorMessage} (已重试{RetryCount}次，将继续重试...)", errorMessage, retryCount);
                }
            }
            catch (Exception ex)
            {
                retryCount++;
                Log.Error(ex, "📸 [连接管理器] ❌ 连接时发生未预期错误 (第{RetryCount}次重试)", retryCount);
            }
            
            // 如果连接失败，清理资源并计算重试延迟
            if (_client?.Connected != true)
            {
                _stream?.Dispose();
                _client?.Dispose();
                _client = null;
                _stream = null;
                
                ConnectionChanged?.Invoke("相机设备", false);
                
                // 指数退避重连策略：1s, 2s, 4s, 8s, 16s, 30s(最大)
                var retryDelay = Math.Min(baseRetryDelay * Math.Pow(2, Math.Min(retryCount - 1, 4)), maxRetryDelay);
                
                if (retryCount <= 3)
                {
                    Log.Information("📸 [连接管理器] 将在{DelaySeconds}秒后重试连接...", retryDelay / 1000.0);
                }
                else if (retryCount % 10 == 0)
                {
                    Log.Information("📸 [连接管理器] 将在{DelaySeconds}秒后继续重试连接... (提示：请确保相机设备已启动)", retryDelay / 1000.0);
                }
                
                try
                {
                    await Task.Delay((int)retryDelay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        
        Log.Information("📸 [连接管理器] 已停止");
    }

    /// <summary>
    /// 【新增】数据接收任务
    /// </summary>
    private async Task ReceiveDataAsync(CancellationToken cancellationToken)
    {
        if (_stream == null)
        {
            Log.Warning("📸 [数据接收] 网络流为空，无法接收数据");
            return;
        }
        
        Log.Information("📸 [数据接收] 开始接收相机数据");
        ConnectionChanged?.Invoke("相机设备", true);
        
        var buffer = new byte[8192];
        var consecutiveErrors = 0;
        var maxConsecutiveErrors = 3;
        
        try
        {
            while (!cancellationToken.IsCancellationRequested && _client?.Connected == true)
            {
                try
                {
                    var bytesRead = await _stream.ReadAsync(buffer, cancellationToken);
                    
                    if (bytesRead == 0)
                    {
                        Log.Information("📸 [数据接收] 相机设备关闭了连接，将触发重连");
                        break;
                    }
                    
                    // 重置连续错误计数
                    consecutiveErrors = 0;
                    
                    // 【关键修复】立即记录数据到达时间戳，避免后续操作影响
                    var receiveTimestamp = DateTimeOffset.UtcNow;
                    
                    var dataCopy = new byte[bytesRead];
                    Array.Copy(buffer, 0, dataCopy, 0, bytesRead);
                    
                    // 将数据加入处理队列
                    var writeSuccess = _dataWriter.TryWrite((dataCopy, receiveTimestamp));
                    if (!writeSuccess)
                    {
                        Log.Warning("📸 [数据接收] 数据入队失败，队列可能已满或已关闭");
                    }
                    else
                    {
                        Log.Debug("📸 [数据接收] 成功接收 {BytesRead} 字节数据", bytesRead);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Log.Debug("📸 [数据接收] 接收任务被取消");
                    break;
                }
                catch (IOException ioEx) when (ioEx.InnerException is SocketException sockEx)
                {
                    consecutiveErrors++;
                    
                    // 根据Socket错误类型提供友好提示
                    string errorMessage = sockEx.SocketErrorCode switch
                    {
                        SocketError.ConnectionReset => "连接被重置",
                        SocketError.ConnectionAborted => "连接被中止", 
                        SocketError.NetworkDown => "网络已断开",
                        SocketError.NetworkUnreachable => "网络不可达",
                        SocketError.TimedOut => "操作超时",
                        _ => $"Socket错误: {sockEx.SocketErrorCode}"
                    };
                    
                    Log.Warning("📸 [数据接收] 网络异常: {ErrorMessage} (连续错误: {ConsecutiveErrors}/{MaxErrors})", 
                        errorMessage, consecutiveErrors, maxConsecutiveErrors);
                    
                    if (consecutiveErrors >= maxConsecutiveErrors)
                    {
                        Log.Error("📸 [数据接收] 连续网络错误过多，停止接收并触发重连");
                        break;
                    }
                    
                    // 短暂等待后继续尝试
                    await Task.Delay(1000, cancellationToken);
                }
                catch (Exception ex)
                {
                    consecutiveErrors++;
                    Log.Error(ex, "📸 [数据接收] 接收数据时发生未预期错误 (连续错误: {ConsecutiveErrors}/{MaxErrors})", 
                        consecutiveErrors, maxConsecutiveErrors);
                    
                    if (consecutiveErrors >= maxConsecutiveErrors)
                    {
                        Log.Error("📸 [数据接收] 连续错误过多，停止接收并触发重连");
                        break;
                    }
                    
                    // 短暂等待后继续尝试
                    await Task.Delay(1000, cancellationToken);
                }
            }
        }
        finally
        {
            Log.Information("📸 [数据接收] 数据接收任务已结束");
            ConnectionChanged?.Invoke("相机设备", false);
            
            // 关闭当前连接，触发重连
            try
            {
                _stream?.Dispose();
                _client?.Close();
                Log.Debug("📸 [数据接收] 连接资源已清理，连接管理器将尝试重连");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "📸 [数据接收] 清理连接资源时发生错误");
            }
        }
    }

    /// <summary>
    /// 【终极修复】同步版本的数据队列处理，确保始终在专用线程上执行，避免async/await导致的线程切换
    /// </summary>
    private void ProcessDataQueueSync(CancellationToken token)
    {
        Log.Information("🚀 [专用线程] 同步数据处理循环已启动，确保无线程切换。");
        
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // 【线程状态监控】确认当前仍在专用线程上
                    var currentThread = Thread.CurrentThread;
                    if (currentThread.IsThreadPoolThread)
                    {
                        Log.Error("🚨 [致命错误] 专用线程被切换到线程池线程: ID={ThreadId}, 名称='{ThreadName}'", 
                            currentThread.ManagedThreadId, currentThread.Name ?? "未命名");
                    }
                    
                    // 【紧急诊断】监控专用线程的数据读取响应时间
                    bool hasProcessedData = false;

                    // 处理所有可用数据
                    while (_dataReader.TryRead(out var item))
                    {
                        hasProcessedData = true;
                        var currentTime = DateTimeOffset.UtcNow;
                        var dataAge = (currentTime - item.timestamp).TotalMilliseconds;
                        
                        // 【精确时间追踪】记录关键时间点
                        Log.Debug("⏱️  [专用线程同步] 数据时间戳={DataTimestamp:HH:mm:ss.fff}, 当前时间={CurrentTime:HH:mm:ss.fff}, 队列等待={DataAge:F0}ms", 
                            item.timestamp, currentTime, dataAge);
                        
                        if (dataAge > 50)
                        {
                            Log.Warning("数据在队列中等待时间过长: {DataAge:F0}ms", dataAge);
                        }
                        
                        Log.Debug("开始处理队列数据，数据年龄: {DataAge:F0}ms", dataAge);
                        
                        // 【精确计时】HandleDataReceived 的执行时间
                        var handleStartTime = DateTimeOffset.UtcNow;
                        HandleDataReceived(item.data, item.timestamp);
                        var handleDuration = (DateTimeOffset.UtcNow - handleStartTime).TotalMilliseconds;
                        
                        Log.Debug("⏱️  [专用线程HandleDataReceived] 执行耗时: {HandleDuration:F0}ms", handleDuration);
                    }
                    
                    // 【关键修复】如果没有处理任何数据，使用Thread.Yield()让出CPU时间片
                    // Thread.Yield()比Thread.Sleep(1)更高效，几乎无延迟
                    if (!hasProcessedData)
                    {
                        Thread.Yield(); // 让出时间片给其他线程，然后立即重新调度
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    Log.Debug("专用线程数据处理收到取消请求");
                    break;
                }
                catch (InvalidOperationException)
                {
                    // Channel已完成
                    Log.Debug("数据队列已完成（专用线程检测）");
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "专用线程处理数据项时发生错误");
                    // 短暂延迟避免紧密循环，但不使用异步
                    Thread.Yield();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { 
            Log.Debug("专用线程数据处理队列已取消。"); 
        }
        catch (Exception ex) 
        { 
            Log.Error(ex, "专用线程数据处理发生致命错误。"); 
        }
        finally
        {
            Log.Information("🚀 [专用线程] 同步数据处理循环已停止。");
        }
    }

    /// <summary>
    /// 【终极修复】独立的包裹发布线程，彻底隔离Subject背压影响
    /// </summary>
    private void ProcessPublishQueueSync(CancellationToken token)
    {
        Log.Information("🚀 [发布线程] 同步包裹发布循环已启动，彻底隔离Subject背压。");
        
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    bool hasPublishedData = false;

                    // 处理所有待发布的包裹
                    while (_publishReader.TryRead(out var timestampedPackage))
                    {
                        hasPublishedData = true;
                        var currentTime = DateTimeOffset.UtcNow;
                        var publishAge = (currentTime - timestampedPackage.Timestamp).TotalMilliseconds;
                        
                        Log.Debug("⏱️  [发布线程] 包裹={Barcode}, 创建时间={CreateTime:HH:mm:ss.fff}, 发布延迟={PublishAge:F0}ms", 
                            timestampedPackage.Value.Barcode, timestampedPackage.Timestamp, publishAge);
                        
                        if (publishAge > 100)
                        {
                            Log.Warning("包裹发布延迟过高: {PublishAge:F0}ms, 条码={Barcode}", publishAge, timestampedPackage.Value.Barcode);
                        }
                        
                        try
                        {
                            // 【彻底隔离】在独立线程上发布Subject，避免任何可能的背压
                            var publishStartTime = DateTimeOffset.UtcNow;
                            _packageTimestampedSubject.OnNext(timestampedPackage);
                            var publishDuration = (DateTimeOffset.UtcNow - publishStartTime).TotalMilliseconds;
                            
                            if (publishDuration > 50)
                            {
                                Log.Warning("Subject.OnNext耗时异常: {PublishDuration:F0}ms, 条码={Barcode}", publishDuration, timestampedPackage.Value.Barcode);
                            }
                            else
                            {
                                Log.Debug("包裹成功发布: 条码={Barcode}, Subject发布耗时={PublishDuration:F0}ms", 
                                    timestampedPackage.Value.Barcode, publishDuration);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "发布包裹到Subject时发生错误: 条码={Barcode}", timestampedPackage.Value.Barcode);
                            // 即使发布失败也继续处理其他包裹，不影响整个发布流程
                        }
                    }
                    
                    // 如果没有处理任何数据，让出CPU时间片
                    if (!hasPublishedData)
                    {
                        Thread.Yield();
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    Log.Debug("发布线程收到取消请求");
                    break;
                }
                catch (InvalidOperationException)
                {
                    // Channel已完成
                    Log.Debug("发布队列已完成（发布线程检测）");
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "发布线程处理包裹时发生错误");
                    Thread.Yield();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { 
            Log.Debug("发布线程已取消。"); 
        }
        catch (Exception ex) 
        { 
            Log.Error(ex, "发布线程发生致命错误。"); 
        }
        finally
        {
            Log.Information("🚀 [发布线程] 同步包裹发布循环已停止。");
        }
    }

    private void HandleDataReceived(byte[] data, DateTimeOffset receiveTimestamp)
    {
        var startTime = DateTimeOffset.UtcNow;
        var totalDelay = (startTime - receiveTimestamp).TotalMilliseconds;
        
        try
        {
            Log.Debug("🔬 [HandleDataReceived开始] 接收时间={ReceiveTime:HH:mm:ss.fff}, 处理开始时间={StartTime:HH:mm:ss.fff}, 总延迟={TotalDelay:F0}ms", 
                receiveTimestamp, startTime, totalDelay);
            
            var receivedString = Encoding.UTF8.GetString(data);
            var decodeTime = DateTimeOffset.UtcNow;
            var decodeDelay = (decodeTime - startTime).TotalMilliseconds;
            
            Log.Debug("专用线程处理数据片段: {Data} (总处理延迟: {Delay:F0}ms, UTF8解码: {DecodeDelay:F0}ms)", 
                receivedString, totalDelay, decodeDelay);
            
            _receiveBuffer.Append(receivedString);
            
            // 添加缓冲区状态日志
            Log.Debug("缓冲区当前长度: {Length}, 内容: {Content}", _receiveBuffer.Length, _receiveBuffer.ToString());

            if (_receiveBuffer.Length > MaxBufferSize)
            {
                Log.Warning("接收缓冲区大小超过限制，清空缓冲区");
                _receiveBuffer.Clear();
                return;
            }

            string bufferContent = _receiveBuffer.ToString();
            int lastDelimiter = bufferContent.LastIndexOf(';');
            if (lastDelimiter == -1)
            {
                Log.Debug("未找到结束符;，直接丢弃数据: {BufferContent}", bufferContent);
                _receiveBuffer.Clear(); // 直接清空缓冲区，丢弃数据
                return;
            }
            
            string processablePart = bufferContent[..(lastDelimiter + 1)];
            string remainder = bufferContent[(lastDelimiter + 1)..];
            
            Log.Debug("找到{Count}个完整的数据包，剩余数据: {Remainder}", processablePart.Count(c => c == ';'), remainder);
            
            var packets = processablePart.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var packet in packets)
            {
                var packetStartTime = DateTimeOffset.UtcNow;
                ProcessPackageData(packet, receiveTimestamp);
                var packetProcessTime = (DateTimeOffset.UtcNow - packetStartTime).TotalMilliseconds;
                if (packetProcessTime > 10)
                {
                    Log.Warning("包裹数据处理耗时异常: {ProcessTime:F0}ms, 数据: {Packet}", packetProcessTime, packet);
                }
            }
            
            _receiveBuffer.Clear();
            _receiveBuffer.Append(remainder);
            
            var totalProcessTime = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
            if (totalProcessTime > 50)
            {
                Log.Warning("数据处理总耗时异常: {TotalTime:F0}ms", totalProcessTime);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理接收到的TCP数据时发生错误");
            _receiveBuffer.Clear();
        }
    }

    private void ProcessPackageData(string packetData, DateTimeOffset receiveTimestamp)
    {
        var partsList = packetData.Split(',').Select(s => s.Trim()).ToList();
        
        if (!ValidatePacket(partsList))
        {
            Log.Warning("无效的包裹数据: '{PacketData}'", packetData);
            return;
        }

        try
        {
            var code = partsList[0];

            // 【协议修正】解析秒级时间戳并计算延迟
            _ = long.TryParse(partsList[6], out var sendTimestampSec);
            var sendTimestampMs = sendTimestampSec * 1000; // 转换为毫秒进行计算
            var networkLatency = receiveTimestamp.ToUnixTimeMilliseconds() - sendTimestampMs;

            if (networkLatency < 0)
            {
                Log.Warning("收到相机数据，但计算出的网络延迟为负数({Latency}ms)，可能时钟不同步。条码={Barcode}", networkLatency, code);
            }

            var package = PackageInfo.Create(); // 系统内部自动生成新的GUID
            package.SetBarcode(code);

            if (float.TryParse(partsList[1], out var weight)) package.Weight = weight;
            if (double.TryParse(partsList[2], out var length) && 
                double.TryParse(partsList[3], out var width) &&
                double.TryParse(partsList[4], out var height))
            {
                package.SetDimensions(length, width, height);
                if (double.TryParse(partsList[5], out var volume)) package.Volume = volume;
            }
            
            // partsList[6] 是来自发送方的秒级时间戳，此处我们使用更精确的服务器接收时间戳 receiveTimestamp
            
            if (string.Equals(code, "noread", StringComparison.OrdinalIgnoreCase))
            {
                package.SetStatus("无法识别条码");
                Log.Information("收到无法识别条码的包裹: GUID={Guid}, 网络延迟={Latency:F0}ms, 发送时间戳={SendTimestamp}秒", package.Guid, networkLatency, sendTimestampSec);
            }
            else
            {
                Log.Information("收到包裹: GUID={Guid}, 条码={Barcode}, 网络延迟={Latency:F0}ms, 发送时间戳={SendTimestamp}秒", package.Guid, code, networkLatency, sendTimestampSec);
            }

            // 使用接收时的时间戳，而不是处理时的时间戳
            var timestampedPackage = new Timestamped<PackageInfo>(package, receiveTimestamp);
            
            // 【终极修复】将包裹发布到独立的发布队列，彻底避免Subject背压阻塞数据处理线程
            var publishSuccess = _publishWriter.TryWrite(timestampedPackage);
            if (!publishSuccess)
            {
                Log.Warning("包裹发布队列已满或已关闭，无法发布包裹: 条码={Barcode}", package.Barcode);
            }
            else
            {
                Log.Debug("包裹已成功加入发布队列: 条码={Barcode}", package.Barcode);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理单个包裹数据时发生错误: {Data}", packetData);
        }
    }
    
    private static bool ValidatePacket(List<string> packetParts)
    {
        // 协议格式: {code},{weight},{length},{width},{height},{volume},{sendTimestamp(秒)}; -> 7个部分
        if (packetParts.Count != 7) return false;
        
        if (string.IsNullOrEmpty(packetParts[0].Trim())) return false; // code
        if (!float.TryParse(packetParts[1], out _)) return false;    // weight
        if (!double.TryParse(packetParts[2], out _)) return false;   // length
        if (!double.TryParse(packetParts[3], out _)) return false;   // width
        if (!double.TryParse(packetParts[4], out _)) return false;   // height
        if (!double.TryParse(packetParts[5], out _)) return false;   // volume
        if (!long.TryParse(packetParts[6], out _)) return false;     // sendTimestamp(秒)
        return true;
    }


}