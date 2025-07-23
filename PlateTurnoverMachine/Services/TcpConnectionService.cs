using System.IO;
using System.Net.Sockets;
using DongtaiFlippingBoardMachine.Models;
using Serilog;

namespace DongtaiFlippingBoardMachine.Services;

/// <summary>
///     TCP连接服务实现
/// </summary>
internal class TcpConnectionService : ITcpConnectionService
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Dictionary<TcpConnectionConfig, TcpClient> _tcpModuleClients = [];
    private readonly Dictionary<TcpConnectionConfig, CancellationTokenSource> _tcpModuleListeningCts = [];
    private readonly Dictionary<TcpConnectionConfig, Task> _tcpModuleListeningTasks = [];
    private bool _disposed;
    private TcpClient? _triggerPhotoelectricClient;
    private CancellationTokenSource? _triggerPhotoelectricListeningCts;
    private Task? _triggerPhotoelectricListeningTask;

    /// <summary>
    ///     TCP模块连接状态改变事件
    /// </summary>
    public event EventHandler<(TcpConnectionConfig Config, bool Connected)>? TcpModuleConnectionChanged;

    /// <summary>
    ///     触发光电连接状态改变事件
    /// </summary>
    public event EventHandler<bool>? TriggerPhotoelectricConnectionChanged;

    /// <summary>
    ///     获取触发光电的TCP客户端
    /// </summary>
    public TcpClient? TriggerPhotoelectricClient
    {
        get => _triggerPhotoelectricClient;
        private set
        {
            if (_triggerPhotoelectricClient == value) return;

            _triggerPhotoelectricClient = value;
            TriggerPhotoelectricConnectionChanged?.Invoke(this, value?.Connected ?? false);
        }
    }

    /// <summary>
    ///     获取TCP模块客户端字典
    /// </summary>
    public IReadOnlyDictionary<TcpConnectionConfig, TcpClient> TcpModuleClients
    {
        get => _tcpModuleClients;
    }

    /// <summary>
    ///     触发光电数据接收事件
    /// </summary>
    public event EventHandler<TcpDataReceivedEventArgs>? TriggerPhotoelectricDataReceived;

    /// <summary>
    ///     TCP模块数据接收事件
    /// </summary>
    public event EventHandler<TcpModuleDataReceivedEventArgs>? TcpModuleDataReceived;

    /// <summary>
    ///     连接触发光电
    /// </summary>
    /// <param name="config">连接配置</param>
    /// <returns>连接是否成功</returns>
    public async Task<bool> ConnectTriggerPhotoelectricAsync(TcpConnectionConfig config)
    {
        if (_disposed)
        {
            Log.Warning("TcpConnectionService 已释放，无法连接触发光电: {Config}", config.IpAddress);
            return false;
        }

        try
        {
            // 关闭已有连接
            if (TriggerPhotoelectricClient?.Connected == true)
            {
                Log.Information("关闭现有触发光电连接");
                TriggerPhotoelectricClient.Close();
            }

            var client = new TcpClient();

            // 设置连接超时
            var connectTask = client.ConnectAsync(config.GetIpEndPoint());
            var timeoutTask = Task.Delay(10000); // 10秒超时

            if (await Task.WhenAny(connectTask, timeoutTask) == timeoutTask)
            {
                return false;
            }

            // 确保连接任务完成
            await connectTask;

            if (!client.Connected)
            {
                Log.Warning("连接触发光电失败，TcpClient.Connected = false: {Config}", config.IpAddress);
                return false;
            }

            TriggerPhotoelectricClient = client;
            Log.Information("成功连接到触发光电: {Config}, Connected={Connected}", config.IpAddress, client.Connected);

            // 连接成功后自动开始监听数据
            try
            {
                await StartListeningTriggerPhotoelectricAsync(CancellationToken.None);
                Log.Information("已开始监听触发光电数据");
            }
            catch (Exception listenEx)
            {
                Log.Error(listenEx, "开始监听触发光电数据失败");
                // 监听失败不影响连接状态
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "连接触发光电失败: {Config}, 错误信息: {Message}", config.IpAddress, ex.Message);

            // 确保客户端为null，并触发断开连接事件
            if (TriggerPhotoelectricClient != null)
            {
                try
                {
                    TriggerPhotoelectricClient.Close();
                }
                catch
                {
                    /* 忽略关闭异常 */
                }

                TriggerPhotoelectricClient = null;
            }

            return false;
        }
    }

    /// <summary>
    ///     连接TCP模块
    /// </summary>
    /// <returns>连接结果字典，key为配置，value为对应的TcpClient</returns>
    public async Task<Dictionary<TcpConnectionConfig, TcpClient>> ConnectTcpModulesAsync(
        IEnumerable<TcpConnectionConfig> desiredConfigs)
    {
        if (_disposed)
        {
            Log.Warning("TcpConnectionService 已释放，无法连接TCP模块");
            return []; // 返回空字典
        }

        var uniqueDesiredConfigs = desiredConfigs.Distinct().ToList();
        var currentClients = _tcpModuleClients.Keys.ToList();

        // 2. 识别需要断开的模块（当前已连接，但不在新的期望列表中）
        var configsToDisconnect = currentClients
            .Where(c => !uniqueDesiredConfigs.Contains(c))
            .ToList();

        foreach (var config in configsToDisconnect)
        {
            Log.Information("配置已移除，正在断开TCP模块: {Config}", config.IpAddress);
            if (_tcpModuleClients.TryGetValue(config, out var client))
            {
                await StopListeningTcpModuleAsync(config);
                client.Close();
                _tcpModuleClients.Remove(config);
                OnTcpModuleConnectionChanged(config, false);
            }
        }

        // 1. 识别需要连接的模块（在期望列表中，但当前未连接或已断开）
        var configsToConnect = uniqueDesiredConfigs
            .Where(c => !_tcpModuleClients.TryGetValue(c, out var client) || !client.Connected)
            .ToList();

        var newConnections = new Dictionary<TcpConnectionConfig, TcpClient>();
        if (!configsToConnect.Any())
        {
            return newConnections; // 没有需要连接的，直接返回
        }

        Log.Information("检测到 {Count} 个需要连接/重连的TCP模块。", configsToConnect.Count);

        foreach (var config in configsToConnect)
        {
            try
            {
                // 如果存在旧的、已断开的客户端实例，先移除
                if (_tcpModuleClients.ContainsKey(config))
                {
                    await StopListeningTcpModuleAsync(config); // 确保旧的监听任务已停止
                    _tcpModuleClients.Remove(config);
                }

                var client = new TcpClient();
                await client.ConnectAsync(config.GetIpEndPoint());

                _tcpModuleClients.Add(config, client);
                newConnections.Add(config, client);

                Log.Information("成功连接到TCP模块: {Config}", config.IpAddress);
                OnTcpModuleConnectionChanged(config, true);

                // 连接成功后自动开始监听数据
                await StartListeningTcpModuleAsync(config, client, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "连接TCP模块失败: {Config}", config.IpAddress);
                OnTcpModuleConnectionChanged(config, false); // 确保在失败时也触发状态变更
            }
        }
        return newConnections;
    }

    /// <summary>
    ///     发送数据到指定的TCP模块
    /// </summary>
    /// <param name="config">TCP模块配置</param>
    /// <param name="data">要发送的数据</param>
    public Task SendToTcpModuleAsync(TcpConnectionConfig config, byte[] data)
    {
        if (!_tcpModuleClients.TryGetValue(config, out var client) || !client.Connected)
            throw new InvalidOperationException($"TCP模块未连接: {config.IpAddress}");

        return SendDataAsync(client, data);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     开始监听触发光电数据
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <exception cref="InvalidOperationException">触发光电未连接时抛出此异常</exception>
    private Task StartListeningTriggerPhotoelectricAsync(CancellationToken cancellationToken)
    {
        if (TriggerPhotoelectricClient is not { Connected: true })
        {
            Log.Warning("尝试开始监听触发光电数据，但触发光电未连接");
            throw new InvalidOperationException("触发光电未连接");
        }

        StopListeningTriggerPhotoelectric(); // 停止现有任务

        _triggerPhotoelectricListeningCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var localCts = _triggerPhotoelectricListeningCts;
        var localClient = TriggerPhotoelectricClient;

        _triggerPhotoelectricListeningTask = Task.Run(async () =>
        {
            Log.Information("启动触发光电监听任务");
            var buffer = new byte[1024]; // 独立缓冲区

            try
            {
                var stream = localClient.GetStream();
                while (!localCts.Token.IsCancellationRequested && localClient.Connected)
                {
                    try
                    {
                        Log.Debug("触发光电: 等待数据...");
                        var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, localCts.Token);
                        var receivedTime = DateTime.Now;

                        if (bytesRead == 0)
                        {
                            Log.Warning("触发光电连接似乎已关闭 (读取到0字节)");
                            TriggerPhotoelectricConnectionChanged?.Invoke(this, false);
                            break;
                        }

                        Log.Debug("触发光电: 接收到 {BytesRead} 字节", bytesRead);
                        var data = new byte[bytesRead];
                        Array.Copy(buffer, data, bytesRead);

                        // 触发事件
                        TriggerPhotoelectricDataReceived?.Invoke(this,
                            new TcpDataReceivedEventArgs(data, receivedTime));
                    }
                    catch (IOException ex) when (ex.InnerException is SocketException socketEx)
                    {
                        if (socketEx.SocketErrorCode == SocketError.TimedOut)
                        {
                            Log.Verbose("触发光电: 读取超时");
                            continue;
                        }

                        Log.Error(ex, "触发光电: 读取时发生IO错误 (SocketErrorCode: {ErrorCode})，连接将关闭", socketEx.SocketErrorCode);
                        TriggerPhotoelectricConnectionChanged?.Invoke(this, false);
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        Log.Information("触发光电监听任务已取消");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "触发光电监听任务发生未处理异常，连接将关闭");
                        TriggerPhotoelectricConnectionChanged?.Invoke(this, false);
                        break;
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                Log.Information("触发光电监听任务因对象已释放而停止");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "启动触发光电监听任务时发生严重错误");
            }
            finally
            {
                Log.Information("触发光电监听任务结束");
                if (!localClient.Connected)
                {
                    TriggerPhotoelectricConnectionChanged?.Invoke(this, false);
                }
                // 不在此处关闭 Client 或 Stream，由 Dispose 或 Connect 方法管理
            }
        }, localCts.Token);

        return Task.CompletedTask;
    }

    /// <summary>
    ///     停止监听触发光电数据
    /// </summary>
    private void StopListeningTriggerPhotoelectric()
    {
        try
        {
            if (_triggerPhotoelectricListeningCts != null)
            {
                if (!_triggerPhotoelectricListeningCts.IsCancellationRequested)
                    _triggerPhotoelectricListeningCts.Cancel();
                _triggerPhotoelectricListeningCts.Dispose();
                _triggerPhotoelectricListeningCts = null;
            }

            // 等待任务结束
            if (_triggerPhotoelectricListeningTask?.IsCompleted == false)
            {
                Log.Debug("等待触发光电监听任务结束...");
                try
                {
                    if (!_triggerPhotoelectricListeningTask.Wait(TimeSpan.FromSeconds(5)))
                        Log.Warning("等待触发光电监听任务结束超时");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "等待触发光电监听任务结束时发生异常 (可能已取消)");
                }
            }

            _triggerPhotoelectricListeningTask = null;

            Log.Information("已停止监听触发光电数据");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止监听触发光电数据时发生错误");
        }
    }

    /// <summary>
    ///     开始监听指定TCP模块的数据
    /// </summary>
    /// <param name="config">TCP模块配置</param>
    /// <param name="client">TCP客户端</param>
    /// <param name="cancellationToken">取消令牌</param>
    private async Task StartListeningTcpModuleAsync(TcpConnectionConfig config, TcpClient client,
        CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            Log.Warning("TcpConnectionService 已释放，无法启动TCP模块监听: {Config}", config.IpAddress);
            return;
        }

        if (!client.Connected)
        {
            Log.Warning("尝试启动TCP模块监听，但客户端未连接: {Config}", config.IpAddress);
            return;
        }

        await StopListeningTcpModuleAsync(config); // 停止现有任务

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _tcpModuleListeningCts[config] = cts;
        var localClient = client;
        var localConfig = config;

        var task = Task.Run(async () =>
        {
            Log.Information("启动TCP模块监听任务: {Config}", localConfig.IpAddress);
            var buffer = new byte[1024]; // 独立缓冲区

            try
            {
                var stream = localClient.GetStream();
                while (!cts.Token.IsCancellationRequested && localClient.Connected)
                {
                    try
                    {
                        Log.Debug("TCP模块 {Config}: 等待数据...", localConfig.IpAddress);
                        var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                        var receivedTime = DateTime.Now;

                        if (bytesRead == 0)
                        {
                            Log.Warning("TCP模块 {Config} 连接似乎已关闭 (读取到0字节)", localConfig.IpAddress);
                            OnTcpModuleConnectionChanged(localConfig, false);
                            break;
                        }

                        Log.Debug("TCP模块 {Config}: 接收到 {BytesRead} 字节", localConfig.IpAddress, bytesRead);
                        var data = new byte[bytesRead];
                        Array.Copy(buffer, data, bytesRead);

                        // 触发事件
                        TcpModuleDataReceived?.Invoke(this,
                            new TcpModuleDataReceivedEventArgs(localConfig, data, receivedTime));
                    }
                    catch (IOException ex) when (ex.InnerException is SocketException socketEx)
                    {
                        if (socketEx.SocketErrorCode == SocketError.TimedOut)
                        {
                            Log.Verbose("TCP模块 {Config}: 读取超时", localConfig.IpAddress);
                            continue;
                        }

                        Log.Error(ex, "TCP模块 {Config}: 读取时发生IO错误 (SocketErrorCode: {ErrorCode})，连接将关闭",
                            localConfig.IpAddress, socketEx.SocketErrorCode);
                        OnTcpModuleConnectionChanged(localConfig, false);
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        Log.Information("TCP模块监听任务已取消: {Config}", localConfig.IpAddress);
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "TCP模块监听任务发生未处理异常: {Config}，连接将关闭", localConfig.IpAddress);
                        OnTcpModuleConnectionChanged(localConfig, false);
                        break;
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                Log.Information("TCP模块监听任务因对象已释放而停止: {Config}", localConfig.IpAddress);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "启动TCP模块监听任务时发生严重错误: {Config}", localConfig.IpAddress);
            }
            finally
            {
                Log.Information("TCP模块监听任务结束: {Config}", localConfig.IpAddress);
                if (!localClient.Connected)
                {
                    OnTcpModuleConnectionChanged(localConfig, false);
                }
                // 不在此处关闭 Client 或 Stream，由 Dispose 或 Connect 方法管理
            }
        }, cts.Token);

        _tcpModuleListeningTasks[config] = task;
        // return Task.CompletedTask; // Task.Run returns a Task, no need for this
    }

    /// <summary>
    ///     停止监听指定TCP模块的数据
    /// </summary>
    /// <param name="config">TCP模块配置</param>
    private Task StopListeningTcpModuleAsync(TcpConnectionConfig config)
    {
        try
        {
            if (_tcpModuleListeningCts.TryGetValue(config, out var cts))
            {
                if (!cts.IsCancellationRequested) cts.Cancel();
                cts.Dispose();
                _tcpModuleListeningCts.Remove(config);
            }

            if (_tcpModuleListeningTasks.TryGetValue(config, out var task))
            {
                if (!task.IsCompleted)
                {
                    Log.Debug("等待TCP模块监听任务结束: {Config}", config.IpAddress);
                    try
                    {
                        if (!task.Wait(TimeSpan.FromSeconds(5)))
                            Log.Warning("等待TCP模块监听任务结束超时: {Config}", config.IpAddress);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "等待TCP模块监听任务结束时发生异常: {Config} (可能已取消)", config.IpAddress);
                    }
                }

                _tcpModuleListeningTasks.Remove(config);
            }

            Log.Information("已停止监听TCP模块数据: {Config}", config.IpAddress);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止监听TCP模块数据时发生错误: {Config}", config.IpAddress);
        }

        return Task.CompletedTask;
    }

    private async Task SendDataAsync(TcpClient client, byte[] data)
    {
        await _sendLock.WaitAsync();
        try
        {
            var stream = client.GetStream();
            await stream.WriteAsync(data);
            await stream.FlushAsync();
        }
        finally
        {
            _sendLock.Release();
        }
    }

    protected void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        _disposed = true;

        if (!disposing) return;
        Log.Information("开始释放 TcpConnectionService 资源...");

        // 停止所有TCP模块的监听
        var moduleConfigs = _tcpModuleClients.Keys.ToList(); // 创建副本以安全迭代
        foreach (var config in moduleConfigs)
        {
            StopListeningTcpModuleAsync(config).Wait();
            // 不需要手动触发 OnTcpModuleConnectionChanged，任务结束时会处理
        }

        _tcpModuleListeningTasks.Clear(); // 清理任务字典
        _tcpModuleListeningCts.Clear(); // 清理CTS字典

        // 停止触发光电监听
        StopListeningTriggerPhotoelectric();

        // 释放锁
        _sendLock.Dispose();

        // 关闭连接
        try
        {
            TriggerPhotoelectricClient?.Close();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "关闭触发光电客户端时出错");
        }

        foreach (var client in _tcpModuleClients.Values)
        {
            try
            {
                client.Close();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "关闭TCP模块客户端时出错: {IpAddress}", client.Client.RemoteEndPoint);
            }
        }

        _tcpModuleClients.Clear();

        Log.Information("TcpConnectionService 资源已释放");
    }

    /// <summary>
    ///     触发TCP模块连接状态改变事件
    /// </summary>
    /// <param name="config">TCP模块配置</param>
    /// <param name="connected">是否已连接</param>
    private void OnTcpModuleConnectionChanged(TcpConnectionConfig config, bool connected)
    {
        TcpModuleConnectionChanged?.Invoke(this, (config, connected));
        Log.Information("TCP模块 {Config} 连接状态改变: {Status}", config.IpAddress, connected ? "已连接" : "已断开");
    }
}