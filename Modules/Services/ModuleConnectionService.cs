using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Common.Models.Package;
using Common.Services.Settings;
using Serilog;
using ShanghaiModuleBelt.Models;

namespace ShanghaiModuleBelt.Services;

internal class ModuleConnectionService(ISettingsService settingsService, ChutePackageRecordService chutePackageRecordService) : IModuleConnectionService
{
    // 数据包相关常量
    private const byte StartCode = 0xF9; // 起始码 16#F9
    private const byte FunctionCodeReceive = 0x10; // 接收包裹序号的功能码 16#10
    private const byte FunctionCodeSend = 0x11; // 发送分拣指令的功能码 16#11
    private const byte FunctionCodeFeedback = 0x12; // 反馈指令的功能码 16#12
    private const int PackageLength = 8; // 数据包长度
    private const byte Checksum = 0xFF; // 固定校验位 16#FF
    private readonly object _bindingLock = new();
    private readonly ModuleConfig _config = settingsService.LoadSettings<ModuleConfig>();
    private readonly ConcurrentDictionary<ushort, string> _packageBindings = new();

    // 包裹处理相关字段
    private readonly ConcurrentDictionary<ushort, bool> _processingPackages = new();
    private readonly ConcurrentDictionary<ushort, PackageWaitInfo> _waitingPackages = new();
    private TcpClient? _connectedClient;
    private bool _isRunning;
    private DateTime _lastProcessTime = DateTime.MinValue;
    private NetworkStream? _networkStream;
    private CancellationTokenSource? _receiveCts;
    private TcpListener? _tcpListener;

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
        try
        {
            Log.Information("处理包裹对象: {Barcode}, 序号={Index}", package.Barcode, package.Index);

            // 记录当前等待队列中的包裹数量
            Log.Debug("当前等待队列中有 {Count} 个包裹等待处理", _waitingPackages.Count);

            // 如果等待队列为空，记录日志
            if (_waitingPackages.IsEmpty)
            {
                Log.Warning("等待队列为空，无法匹配包裹: {Barcode}", package.Barcode);
                return;
            }

            // 遍历所有等待信息
            foreach (var (packageNumber, waitInfo) in _waitingPackages)
            {
                var now = DateTime.Now;
                var timeDiff = (now - waitInfo.ReceiveTime).TotalMilliseconds;

                Log.Debug(
                    "尝试匹配包裹: 序号={PackageNumber}, 条码={Barcode}, 等待时间={TimeDiff}ms, 有效范围={MinWaitTime}-{MaxWaitTime}ms",
                    packageNumber, package.Barcode, timeDiff, _config.MinWaitTime, _config.MaxWaitTime);

                // 检查时间范围
                if (!(timeDiff >= _config.MinWaitTime) || !(timeDiff <= _config.MaxWaitTime))
                {
                    if (timeDiff < _config.MinWaitTime)
                    {
                        Log.Debug("包裹等待时间 {TimeDiff}ms 小于最小等待时间 {MinWaitTime}ms, 跳过匹配",
                            timeDiff, _config.MinWaitTime);
                    }
                    else if (timeDiff > _config.MaxWaitTime)
                    {
                        Log.Debug("包裹等待时间 {TimeDiff}ms 大于最大等待时间 {MaxWaitTime}ms, 标记为超时",
                            timeDiff, _config.MaxWaitTime);
                        package.SetStatus(PackageStatus.Error, "等待超时");
                    }

                    continue;
                }

                // 验证包裹绑定关系
                if (!ValidatePackageBinding(packageNumber, package.Barcode))
                {
                    Log.Debug("包裹绑定验证失败: 序号={PackageNumber}, 条码={Barcode}",
                        packageNumber, package.Barcode);
                    continue;
                }

                // 设置包裹序号为模组带序号
                package.Index = packageNumber;

                Log.Information("找到匹配的等待包裹: 序号={PackageNumber}, 等待时间={TimeDiff}ms, 分配格口={ChuteNumber}",
                    packageNumber, timeDiff, package.ChuteNumber);
                package.ProcessingTime = timeDiff;

                // 取消超时任务
                waitInfo.TimeoutCts?.Cancel();

                // 发送分拣指令
                _ = SendSortingCommandAsync(packageNumber, (byte)package.ChuteNumber);
                chutePackageRecordService.AddPackageRecord(package);
                // 从等待队列中移除
                _waitingPackages.TryRemove(packageNumber, out _);
                return; // 找到匹配后直接返回
            }

            // 如果遍历完所有等待包裹都没有匹配成功，记录日志
            Log.Warning("未找到匹配的等待包裹: 条码={Barcode}, 序号={Index}", package.Barcode, package.Index);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹对象时发生错误: {Barcode}", package.Barcode);
            package.SetStatus(PackageStatus.Error, "处理异常");
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
                    await ProcessPackageNumberAsync(data);
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

    private Task ProcessPackageNumberAsync(byte[] data)
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
                ReceiveTime = DateTime.Now,
                TimeoutCts = new CancellationTokenSource()
            };

            // 检查时序
            lock (_bindingLock)
            {
                var currentTime = DateTime.Now;
                if (currentTime < _lastProcessTime)
                    Log.Warning("检测到时序异常: 当前时间={Current}, 上次处理时间={Last}",
                        currentTime, _lastProcessTime);
                _lastProcessTime = currentTime;
            }

            // 添加到等待队列
            if (!_waitingPackages.TryAdd(packageNumber, waitInfo))
            {
                Log.Warning("包裹序号重复: {PackageNumber}", packageNumber);
                _processingPackages.TryRemove(packageNumber, out _);
                waitInfo.TimeoutCts?.Dispose();
                return Task.CompletedTask;
            }

            // 启动超时处理
            _ = Task.Run(async () =>
            {
                try
                {
                    try
                    {
                        Log.Debug("启动包裹等待超时任务: 序号={PackageNumber}, 最大等待时间={MaxWaitTime}ms",
                            packageNumber, _config.MaxWaitTime);

                        await Task.Delay(_config.MaxWaitTime, waitInfo.TimeoutCts.Token);

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
                                packageNumber, _config.MaxWaitTime, boundBarcode);

                            // 发送异常格口指令
                            await SendSortingCommandAsync(packageNumber, (byte)_config.ExceptionChute);
                        }
                        else
                        {
                            Log.Debug("包裹 {PackageNumber} 已被处理，取消超时处理", packageNumber);
                        }
                    }
                    finally
                    {
                        // 清理状态
                        _processingPackages.TryRemove(packageNumber, out _);
                        _packageBindings.TryRemove(packageNumber, out _);
                        waitInfo.TimeoutCts?.Dispose();
                        Log.Debug("包裹 {PackageNumber} 处理完成，已清理所有状态", packageNumber);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 正常取消，不需要处理
                    Log.Debug("包裹 {PackageNumber} 的超时任务被取消", packageNumber);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理包裹等待异常: {PackageNumber}", packageNumber);
                }
            });
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

        // 如果没有绑定关系，则添加绑定
        lock (_bindingLock)
        {
            if (!_packageBindings.TryAdd(packageNumber, barcode))
            {
                Log.Warning("添加包裹绑定失败: 序号={PackageNumber}, 条码={Barcode}", packageNumber, barcode);
                return false;
            }

            Log.Debug("新增包裹绑定: 序号={PackageNumber}, 条码={Barcode}", packageNumber, barcode);
            return true;
        }
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

    private class PackageWaitInfo
    {
        public DateTime ReceiveTime { get; init; }
        public TaskCompletionSource<bool> ProcessCompleted { get; } = new();
        public TaskCompletionSource<bool>? FeedbackTask { get; } = new();
        public CancellationTokenSource? TimeoutCts { get; init; }
    }
}