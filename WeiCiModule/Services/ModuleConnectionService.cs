using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Common.Models.Package;
using Common.Services.Settings;
using Serilog;
using WeiCiModule.Models.Settings;

namespace WeiCiModule.Services;

/// <summary>
/// 模组带连接服务实现
/// </summary>
internal class ModuleConnectionService : IModuleConnectionService
{
    // 数据包相关常量
    private const byte StartCode = 0xF9; // 起始码 16#F9
    private const byte FunctionCodeReceive = 0x10; // 接收包裹序号的功能码 16#10
    private const byte FunctionCodeSend = 0x11; // 发送分拣指令的功能码 16#11
    private const byte FunctionCodeFeedback = 0x12; // 反馈指令的功能码 16#12
    private const int PackageLength = 8; // 数据包长度
    private const byte Checksum = 0xFF; // 固定校验位 16#FF

    private readonly object _matchLock = new(); // 新增的、用于保护匹配逻辑的专用锁
    private readonly ISettingsService _settingsService;
    private readonly ConcurrentDictionary<ushort, string> _packageBindings = new();
    private readonly ConcurrentDictionary<ushort, bool> _processingPackages = new();
    private readonly ConcurrentDictionary<ushort, PackageWaitInfo> _waitingPackages = new();

    private TcpClient? _connectedClient;
    private bool _isRunning;
    private DateTime _lastProcessTime = DateTime.MinValue;
    private NetworkStream? _networkStream;
    private CancellationTokenSource? _receiveCts;
    private TcpListener? _tcpListener;

    public ModuleConnectionService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
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
            if (!_isRunning) return Task.CompletedTask;

            _isRunning = false;
            _tcpListener?.Stop();

            // 清理所有等待中的包裹
            foreach (var package in _waitingPackages)
                try
                {
                    package.Value.ProcessCompleted.TrySetCanceled();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "清理等待队列时发生错误: {PackageNumber}", package.Key);
                }

            _waitingPackages.Clear();
            _processingPackages.Clear();
            _packageBindings.Clear();

            if (_connectedClient != null)
            {
                _connectedClient.Close();
                _connectedClient = null;
                OnConnectionStateChanged(false);
            }

            Log.Information("TCP服务器已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止TCP服务器时发生错误");
            throw;
        }

        return Task.CompletedTask;
    }

    public void OnPackageReceived(PackageInfo package)
    {
        // 使用新锁保护整个方法的逻辑，防止并发调用导致竞争条件
        lock (_matchLock)
        {
            try
            {
                Log.Information("处理包裹对象: {Barcode}, 序号={Index}", package.Barcode, package.Index);

                // 记录当前等待队列中的包裹数量和详细信息
                Log.Debug("当前等待队列中有 {Count} 个包裹等待处理", _waitingPackages.Count);
                
                if (_waitingPackages.Count > 0)
                {
                    Log.Debug("等待队列详情: {PackageNumbers}", 
                        string.Join(", ", _waitingPackages.Keys.OrderBy(x => x)));
                }

                // 如果等待队列为空，记录日志并更新统计
                if (_waitingPackages.IsEmpty)
                {
                    Log.Warning("等待队列为空，无法匹配包裹: {Barcode}", package.Barcode);
                    package.SetStatus("no waiting package");
                    return;
                }

                // FIFO匹配算法：严格按照触发信号的时间顺序匹配包裹
                // 找到最早的触发信号
                var earliestTrigger = _waitingPackages
                    .OrderBy(x => x.Value.ReceiveTime)
                    .FirstOrDefault();

                if (earliestTrigger.Key == 0) // 默认值，表示没有找到
                {
                    Log.Warning("等待队列中没有有效的触发信号: {Barcode}", package.Barcode);
                    package.SetStatus("no valid trigger");
                    return;
                }

                var packageNumber = earliestTrigger.Key;
                var waitInfo = earliestTrigger.Value;
                var currentTime = DateTime.Now;
                var timeDiff = (currentTime - waitInfo.ReceiveTime).TotalMilliseconds;
                
                // 详细的时间调试信息
                Log.Debug("🕐 时间计算详情: 序号={PackageNumber}, 条码={Barcode}, 实例ID={InstanceId}", packageNumber, package.Barcode, waitInfo.InstanceId);
                Log.Debug("    接收时间: {ReceiveTime}", waitInfo.ReceiveTime.ToString("HH:mm:ss.fff"));
                Log.Debug("    当前时间: {CurrentTime}", currentTime.ToString("HH:mm:ss.fff"));
                Log.Debug("    时间差: {TimeDiff:F0}ms", timeDiff);
                Log.Debug("    有效时间范围: {MinWaitTime}-{MaxWaitTime}ms", GetMinWaitTime(), GetMaxWaitTime());

                Log.Debug("FIFO匹配：最早触发信号 序号={PackageNumber}, 条码={Barcode}, 等待时间={TimeDiff:F0}ms, 有效范围={MinWaitTime}-{MaxWaitTime}ms",
                    packageNumber, package.Barcode, timeDiff, GetMinWaitTime(), GetMaxWaitTime());

                // 检查最早触发信号是否超时
                if (timeDiff > GetMaxWaitTime())
                {
                    Log.Warning("⚠️ 最早触发信号已超时: 序号={PackageNumber}, 等待时间={TimeDiff:F0}ms > {MaxWaitTime}ms, 丢弃并尝试下一个",
                        packageNumber, timeDiff, GetMaxWaitTime());
                    
                    // 移除超时的触发信号
                    _waitingPackages.TryRemove(packageNumber, out _);
                    waitInfo.TimeoutCts?.Cancel();
                    
                    // 递归处理下一个触发信号
                    OnPackageReceived(package);
                    return;
                }

                // 检查是否满足最小等待时间
                if (timeDiff < GetMinWaitTime())
                {
                    Log.Debug("最早触发信号等待时间不足: 序号={PackageNumber}, 等待时间={TimeDiff:F0}ms < {MinWaitTime}ms, 等待更长时间",
                        packageNumber, timeDiff, GetMinWaitTime());
                    package.SetStatus("waiting for min time");
                    return;
                }

                // 验证包裹绑定关系（添加新的绑定）
                if (!ValidatePackageBinding(packageNumber, package.Barcode))
                {
                    Log.Warning("FIFO匹配：包裹绑定验证失败: 序号={PackageNumber}, 条码={Barcode}",
                        packageNumber, package.Barcode);
                    package.SetStatus("binding failed");
                    return;
                }

                // 成功匹配，设置包裹序号为模组带序号
                package.Index = packageNumber;
                package.SetStatus("sorting");

                Log.Information("✅ FIFO匹配成功: 序号={PackageNumber}, 条码={Barcode}, 等待时间={TimeDiff:F0}ms, 分配格口={ChuteNumber}",
                    packageNumber, package.Barcode, timeDiff, package.ChuteNumber);
                package.ProcessingTime = (long)timeDiff;

                // 取消超时任务
                waitInfo.TimeoutCts?.Cancel();

                // 发送分拣指令
                _ = SendSortingCommandAsync(packageNumber, (byte)package.ChuteNumber);

                // 从等待队列中移除，并调用统一的方法清理其他状态
                _waitingPackages.TryRemove(packageNumber, out _);
                CleanUpPackageState(packageNumber, waitInfo);

                Log.Debug("FIFO匹配完成，剩余等待队列: {Count} 个包裹", _waitingPackages.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理包裹对象时发生错误: {Barcode}", package.Barcode);
                package.SetStatus("error");
            }
        }
    }

    private async Task AcceptClientAsync()
    {
        while (_isRunning)
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
            catch (Exception ex)
            {
                if (_isRunning) Log.Error(ex, "接受客户端连接时发生错误");
                break;
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
                            // 处理完整的数据包
                            await ProcessPackageDataAsync(packageBuffer);
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

    private async Task DisconnectClientAsync()
    {
        try
        {
            await _receiveCts?.CancelAsync()!;
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

    private async Task ProcessPackageDataAsync(byte[] data)
    {
        try
        {
            // 记录接收时间戳
            var receiveTime = DateTime.Now;
            
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
                    await ProcessPackageNumberAsync(data, receiveTime);
                    break;

                case FunctionCodeFeedback:
                    // 处理反馈指令数据包（PLC -> PC 确认）
                    await ProcessFeedbackAsync(data);
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

    private Task ProcessPackageNumberAsync(byte[] data, DateTime receiveTime)
    {
        try
        {
            // 解析包裹序号
            var packageNumber = (ushort)(data[2] << 8 | data[3]);
            Log.Information("收到包裹触发信号: 序号={PackageNumber}", packageNumber);

            // 检查是否正在处理中
            if (!_processingPackages.TryAdd(packageNumber, true))
            {
                Log.Warning("包裹序号 {PackageNumber} 正在处理中，忽略重复触发", packageNumber);
                return Task.CompletedTask;
            }

            // 创建包裹等待信息
            var waitInfo = new PackageWaitInfo
            {
                ReceiveTime = receiveTime,
                TimeoutCts = new CancellationTokenSource()
            };
            
            Log.Debug("创建PackageWaitInfo: 序号={PackageNumber}, 接收时间={ReceiveTime}, 实例ID={InstanceId}",
                packageNumber, waitInfo.ReceiveTime.ToString("HH:mm:ss.fff"), waitInfo.InstanceId);

            // 检查时序（已移除锁）
            var currentTime = DateTime.Now;
            if (currentTime < _lastProcessTime)
                Log.Warning("检测到时序异常: 当前时间={Current}, 上次处理时间={Last}",
                    currentTime, _lastProcessTime);
            _lastProcessTime = currentTime;

            // 添加到等待队列
            if (!_waitingPackages.TryAdd(packageNumber, waitInfo))
            {
                Log.Warning("包裹序号重复: {PackageNumber}", packageNumber);
                _processingPackages.TryRemove(packageNumber, out _);
                waitInfo.TimeoutCts?.Dispose();
                return Task.CompletedTask;
            }

            Log.Debug("启动包裹等待超时任务: 序号={PackageNumber}, 最大等待时间={MaxWaitTime}ms",
                packageNumber, GetMaxWaitTime());

            // 直接启动超时任务，避免线程切换延迟
            _ = ProcessPackageTimeoutAsync(packageNumber, waitInfo);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹序号数据包异常: {Data}", BitConverter.ToString(data));
            if (data.Length >= 4)
            {
                var packageNumber = (ushort)(data[2] << 8 | data[3]);
                _processingPackages.TryRemove(packageNumber, out _);
                _packageBindings.TryRemove(packageNumber, out _);
            }
        }

        return Task.CompletedTask;
    }

    private bool ValidatePackageBinding(ushort packageNumber, string? barcode)
    {
        // 处理空条码情况
        barcode ??= string.Empty;

        // 记录尝试验证的包裹绑定
        Log.Debug("验证包裹绑定: 序号={PackageNumber}, 条码={Barcode}", packageNumber, barcode);

        if (_packageBindings.TryGetValue(packageNumber, out var boundBarcode))
        {
            if (boundBarcode == barcode)
            {
                Log.Debug("包裹绑定匹配成功: 序号={PackageNumber}, 条码={Barcode}", packageNumber, barcode);
                return true;
            }

            Log.Warning("包裹绑定不匹配: 序号={PackageNumber}, 当前条码={CurrentBarcode}, 已绑定条码={BoundBarcode}",
                packageNumber, barcode, boundBarcode);
            return false;
        }

        // 检查条码是否已经绑定到其他序号
        var existingBinding = _packageBindings.FirstOrDefault(p => p.Value == barcode);
        if (!string.IsNullOrEmpty(barcode) && existingBinding.Value == barcode)
        {
            Log.Warning("条码已绑定到其他序号: 条码={Barcode}, 当前序号={CurrentNumber}, 已绑定序号={BoundNumber}",
                barcode, packageNumber, existingBinding.Key);
            return false;
        }

        // 如果没有绑定关系，则添加绑定 (已移除锁)
        if (!_packageBindings.TryAdd(packageNumber, barcode))
        {
            Log.Warning("添加包裹绑定失败: 序号={PackageNumber}, 条码={Barcode}", packageNumber, barcode);
            return false;
        }

        Log.Debug("新增包裹绑定: 序号={PackageNumber}, 条码={Barcode}", packageNumber, barcode);
        return true;
    }

    private Task ProcessFeedbackAsync(byte[] data)
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

            // 设置反馈完成
            if (_waitingPackages.TryGetValue(packageNumber, out var waitInfo) && waitInfo.FeedbackTask != null)
            {
                waitInfo.FeedbackTask.TrySetResult(errorCode == 0);
                Log.Debug("已设置包裹 {PackageNumber} 的PLC反馈完成状态", packageNumber);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理反馈指令异常: {Data}", BitConverter.ToString(data));
        }

        return Task.CompletedTask;
    }

    private async Task SendSortingCommandAsync(ushort packageNumber, byte chute)
    {
        if (_networkStream == null) throw new InvalidOperationException("未连接到模组带控制器");

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

            await _networkStream.WriteAsync(command);
            await _networkStream.FlushAsync();

            Log.Debug("发送分拣指令: {Command}", BitConverter.ToString(command));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "发送分拣指令失败: PackageNumber={PackageNumber}, Chute={Chute}",
                packageNumber, chute);
            throw;
        }
    }

    private void OnConnectionStateChanged(bool isConnected)
    {
        ConnectionStateChanged?.Invoke(this, isConnected);
    }

    // 辅助方法获取配置参数
    private int GetMinWaitTime()
    {
        try
        {
            var settings = _settingsService.LoadSettings<ModelsTcpSettings>();
            return settings.MinWaitTime;
        }
        catch
        {
            return 100; // 默认值
        }
    }

    private int GetMaxWaitTime()
    {
        try
        {
            var settings = _settingsService.LoadSettings<ModelsTcpSettings>();
            return settings.MaxWaitTime;
        }
        catch
        {
            return 2000; // 默认值
        }
    }

    private int GetExceptionChute()
    {
        try
        {
            var settings = _settingsService.LoadSettings<ModelsTcpSettings>();
            return settings.ExceptionChute;
        }
        catch
        {
            return 999; // 默认值
        }
    }

    private async Task ProcessPackageTimeoutAsync(ushort packageNumber, PackageWaitInfo waitInfo)
    {
        try
        {
            await Task.Delay(GetMaxWaitTime(), waitInfo.TimeoutCts.Token);

            // 超时处理
            if (_waitingPackages.TryRemove(packageNumber, out _))
            {
                // 检查是否有绑定的条码
                var boundBarcode = "无";
                if (_packageBindings.TryGetValue(packageNumber, out var barcode))
                {
                    boundBarcode = barcode;
                }

                Log.Warning("包裹等待超时: 序号={PackageNumber}, 最大等待时间={MaxWaitTime}ms, 绑定条码={Barcode}",
                    packageNumber, GetMaxWaitTime(), boundBarcode);

                // 发送异常格口指令
                await SendSortingCommandAsync(packageNumber, (byte)GetExceptionChute());
            }
            else
            {
                Log.Debug("包裹 {PackageNumber} 已被处理，取消超时处理", packageNumber);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消，不需要处理
            Log.Debug("包裹 {PackageNumber} 的超时任务被取消", packageNumber);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹超时异常: {PackageNumber}", packageNumber);
        }
        finally
        {
            // 统一清理所有相关状态
            CleanUpPackageState(packageNumber, waitInfo);
        }
    }

    /// <summary>
    ///     统一清理与包裹序号相关的所有状态
    /// </summary>
    private void CleanUpPackageState(ushort packageNumber, PackageWaitInfo waitInfo)
    {
        _processingPackages.TryRemove(packageNumber, out _);
        _packageBindings.TryRemove(packageNumber, out _);
        waitInfo.TimeoutCts?.Dispose();
        Log.Debug("包裹 {PackageNumber} 的处理状态已清理", packageNumber);
    }

    private class PackageWaitInfo
    {
        public DateTime ReceiveTime { get; init; }
        public TaskCompletionSource<bool> ProcessCompleted { get; } = new();
        public TaskCompletionSource<bool>? FeedbackTask { get; } = new();
        public CancellationTokenSource? TimeoutCts { get; init; }
        
        // 添加唯一标识符用于调试
        public string InstanceId { get; } = Guid.NewGuid().ToString("N")[..8];
    }
} 