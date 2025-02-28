using System.Net.Sockets;
using System.IO;
using Presentation_PlateTurnoverMachine.Models;
using Serilog;

namespace Presentation_PlateTurnoverMachine.Services;

/// <summary>
/// TCP连接服务实现
/// </summary>
public class TcpConnectionService : ITcpConnectionService
{
    private readonly Dictionary<TcpConnectionConfig, TcpClient> _tcpModuleClients = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _receiveLock = new(1, 1);
    private bool _disposed;
    private CancellationTokenSource? _listeningCts;
    private Task? _listeningTask;
    private TcpClient? _triggerPhotoelectricClient;
    private readonly Dictionary<TcpConnectionConfig, Task> _tcpModuleListeningTasks = new();
    private readonly Dictionary<TcpConnectionConfig, CancellationTokenSource> _tcpModuleListeningCts = new();

    /// <summary>
    /// TCP模块连接状态改变事件
    /// </summary>
    public event EventHandler<(TcpConnectionConfig Config, bool Connected)>? TcpModuleConnectionChanged;

    /// <summary>
    /// 触发光电连接状态改变事件
    /// </summary>
    public event EventHandler<bool>? TriggerPhotoelectricConnectionChanged;

    /// <summary>
    /// 获取触发光电的TCP客户端
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
    /// 获取TCP模块客户端字典
    /// </summary>
    public IReadOnlyDictionary<TcpConnectionConfig, TcpClient> TcpModuleClients => _tcpModuleClients;

    /// <summary>
    /// 触发光电数据接收事件
    /// </summary>
    public event EventHandler<TcpDataReceivedEventArgs>? TriggerPhotoelectricDataReceived;

    /// <summary>
    /// TCP模块数据接收事件
    /// </summary>
    public event EventHandler<TcpModuleDataReceivedEventArgs>? TcpModuleDataReceived;

    /// <summary>
    /// 连接触发光电
    /// </summary>
    /// <param name="config">连接配置</param>
    /// <returns>连接是否成功</returns>
    public async Task<bool> ConnectTriggerPhotoelectricAsync(TcpConnectionConfig config)
    {
        try
        {
            TriggerPhotoelectricClient?.Close();
            var client = new TcpClient();
            await client.ConnectAsync(config.GetIpEndPoint());
            TriggerPhotoelectricClient = client;
            Log.Information("成功连接到触发光电: {Config}", config.IpAddress);
            
            // 连接成功后自动开始监听数据
            await StartListeningTriggerPhotoelectricAsync(CancellationToken.None);
            
            return true;
            
        }
        catch (Exception ex)
        {
            Log.Error(ex, "连接触发光电失败: {Config}", config.IpAddress);
            TriggerPhotoelectricClient = null;
            return false;
        }
    }

    /// <summary>
    /// 连接TCP模块
    /// </summary>
    /// <param name="configs">TCP模块连接配置列表</param>
    /// <returns>连接结果字典，key为配置，value为对应的TcpClient</returns>
    public async Task<Dictionary<TcpConnectionConfig, TcpClient>> ConnectTcpModulesAsync(IEnumerable<TcpConnectionConfig> configs)
    {
        // 停止并清理旧的监听任务
        foreach (var (config, _) in _tcpModuleListeningTasks)
        {
            await StopListeningTcpModuleAsync(config);
        }
        _tcpModuleListeningTasks.Clear();
        _tcpModuleListeningCts.Clear();

        // 更新当前连接的客户端集合，并触发断开连接事件
        foreach (var (config, client) in _tcpModuleClients)
        {
            client.Close();
            OnTcpModuleConnectionChanged(config, false);
        }
        _tcpModuleClients.Clear();

        var uniqueConfigs = configs.Distinct().ToList();
        var result = new Dictionary<TcpConnectionConfig, TcpClient>();

        foreach (var config in uniqueConfigs)
        {
            try
            {
                var client = new TcpClient();
                await client.ConnectAsync(config.GetIpEndPoint());
                result.Add(config, client);
                _tcpModuleClients.Add(config, client);
                Log.Information("成功连接到TCP模块: {Config}", config.IpAddress);
                
                // 触发连接成功事件
                OnTcpModuleConnectionChanged(config, true);
                
                // 连接成功后自动开始监听数据
                await StartListeningTcpModuleAsync(config, client, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "连接TCP模块失败: {Config}", config.IpAddress);
                OnTcpModuleConnectionChanged(config, false);
            }
        }

        return result;
    }

    /// <summary>
    /// 发送数据到指定的TCP模块
    /// </summary>
    /// <param name="config">TCP模块配置</param>
    /// <param name="data">要发送的数据</param>
    public async Task SendToTcpModuleAsync(TcpConnectionConfig config, byte[] data)
    {
        if (!_tcpModuleClients.TryGetValue(config, out var client) || !client.Connected)
        {
            throw new InvalidOperationException($"TCP模块未连接: {config.IpAddress}");
        }

        await SendDataAsync(client, data);
    }

    /// <summary>
    /// 从触发光电接收数据
    /// </summary>
    /// <returns>接收到的数据</returns>
    /// <exception cref="InvalidOperationException">触发光电未连接时抛出此异常</exception>
    public async Task<byte[]> ReceiveFromTriggerPhotoelectricAsync()
    {
        if (TriggerPhotoelectricClient is not { Connected: true })
        {
            throw new InvalidOperationException("触发光电未连接");
        }

        return await ReceiveDataAsync(TriggerPhotoelectricClient);
    }

    /// <summary>
    /// 从指定的TCP模块接收数据
    /// </summary>
    /// <param name="config">TCP模块配置</param>
    /// <returns>接收到的数据</returns>
    /// <exception cref="InvalidOperationException">TCP模块未连接时抛出此异常</exception>
    public async Task<byte[]> ReceiveFromTcpModuleAsync(TcpConnectionConfig config)
    {
        if (!_tcpModuleClients.TryGetValue(config, out var client) || !client.Connected)
        {
            throw new InvalidOperationException($"TCP模块未连接: {config.IpAddress}");
        }

        return await ReceiveDataAsync(client);
    }

    /// <summary>
    /// 开始监听触发光电数据
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <exception cref="InvalidOperationException">触发光电未连接时抛出此异常</exception>
    public async Task StartListeningTriggerPhotoelectricAsync(CancellationToken cancellationToken)
    {
        if (TriggerPhotoelectricClient is not { Connected: true })
        {
            throw new InvalidOperationException("触发光电未连接");
        }

        // 如果已经在监听，先停止
        StopListeningTriggerPhotoelectric();

        // 创建新的取消令牌源
        _listeningCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 启动监听任务
        _listeningTask = Task.Run(async () =>
        {
            try
            {
                Log.Information("开始监听触发光电数据");
                
                while (!_listeningCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var data = await ReceiveFromTriggerPhotoelectricAsync();
                        var receivedTime = DateTime.Now;
                        
                        // 触发事件
                        TriggerPhotoelectricDataReceived?.Invoke(this, new TcpDataReceivedEventArgs(data, receivedTime));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Log.Error(ex, "接收触发光电数据时发生错误");
                        
                        // 如果连接断开，尝试等待一段时间后继续
                        if (!TriggerPhotoelectricClient.Connected)
                        {
                            Log.Warning("触发光电连接已断开，等待重新连接");
                            await Task.Delay(5000, _listeningCts.Token);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消，不做处理
                Log.Information("触发光电数据监听已取消");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "监听触发光电数据时发生错误");
            }
        }, _listeningCts.Token);

        await Task.CompletedTask;
    }

    /// <summary>
    /// 停止监听触发光电数据
    /// </summary>
    public void StopListeningTriggerPhotoelectric()
    {
        try
        {
            if (_listeningCts != null)
            {
                if (!_listeningCts.IsCancellationRequested)
                {
                    _listeningCts.Cancel();
                }
                _listeningCts.Dispose();
                _listeningCts = null;
            }

            if (_listeningTask != null)
            {
                // 等待任务完成，但设置超时
                if (!_listeningTask.Wait(TimeSpan.FromSeconds(5)))
                {
                    Log.Warning("等待触发光电监听任务结束超时");
                }
                _listeningTask = null;
            }

            Log.Information("已停止监听触发光电数据");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止监听触发光电数据时发生错误");
        }
    }

    /// <summary>
    /// 开始监听指定TCP模块的数据
    /// </summary>
    /// <param name="config">TCP模块配置</param>
    /// <param name="client">TCP客户端</param>
    /// <param name="cancellationToken">取消令牌</param>
    private async Task StartListeningTcpModuleAsync(TcpConnectionConfig config, TcpClient client, CancellationToken cancellationToken)
    {
        // 如果已经在监听，先停止
        await StopListeningTcpModuleAsync(config);

        // 创建新的取消令牌源
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _tcpModuleListeningCts[config] = cts;

        // 启动监听任务
        var task = Task.Run(async () =>
        {
            try
            {
                Log.Information("开始监听TCP模块数据: {Config}", config.IpAddress);
                
                while (!cts.Token.IsCancellationRequested && client.Connected)
                {
                    try
                    {
                        var data = await ReceiveDataAsync(client);
                        var receivedTime = DateTime.Now;
                        
                        // 触发事件
                        TcpModuleDataReceived?.Invoke(this, new TcpModuleDataReceivedEventArgs(config, data, receivedTime));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Log.Error(ex, "接收TCP模块数据时发生错误: {Config}", config.IpAddress);
                        
                        // 如果连接断开，触发事件并退出
                        if (!client.Connected)
                        {
                            Log.Warning("TCP模块连接已断开: {Config}", config.IpAddress);
                            OnTcpModuleConnectionChanged(config, false);
                            break;
                        }
                        
                        // 其他错误，等待一段时间后继续
                        await Task.Delay(1000, cts.Token);
                    }
                }

                // 如果是因为连接断开而退出循环，确保触发事件
                if (!client.Connected)
                {
                    OnTcpModuleConnectionChanged(config, false);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消，不做处理
                Log.Information("TCP模块数据监听已取消: {Config}", config.IpAddress);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "监听TCP模块数据时发生错误: {Config}", config.IpAddress);
            }
        }, cts.Token);

        _tcpModuleListeningTasks[config] = task;
        await Task.CompletedTask;
    }

    /// <summary>
    /// 停止监听指定TCP模块的数据
    /// </summary>
    /// <param name="config">TCP模块配置</param>
    private Task StopListeningTcpModuleAsync(TcpConnectionConfig config)
    {
        try
        {
            if (_tcpModuleListeningCts.TryGetValue(config, out var cts))
            {
                if (!cts.IsCancellationRequested)
                {
                    cts.Cancel();
                }
                cts.Dispose();
                _tcpModuleListeningCts.Remove(config);
            }

            if (_tcpModuleListeningTasks.TryGetValue(config, out var task))
            {
                // 等待任务完成，但设置超时
                if (!task.Wait(TimeSpan.FromSeconds(5)))
                {
                    Log.Warning("等待TCP模块监听任务结束超时: {Config}", config.IpAddress);
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

    private async Task<byte[]> ReceiveDataAsync(TcpClient client)
    {
        await _receiveLock.WaitAsync();
        try
        {
            var stream = client.GetStream();
            
            // 检查是否有可用数据
            if (!stream.DataAvailable)
            {
                // 等待数据可用
                var buffer = new byte[1];
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, 1));
                
                // 如果没有读取到数据，返回空数组
                if (bytesRead == 0)
                {
                    return [];
                }
                
                // 创建结果数组，包含已读取的第一个字节
                var totalAvailable = client.Available + 1;
                var result = new byte[totalAvailable];
                result[0] = buffer[0];
                
                // 读取剩余数据
                if (client.Available <= 0) return [buffer[0]];
                var remainingBuffer = new byte[client.Available];
                var totalBytesRead = 0;
                    
                while (totalBytesRead < remainingBuffer.Length)
                {
                    var read = await stream.ReadAsync(remainingBuffer.AsMemory(totalBytesRead, remainingBuffer.Length - totalBytesRead));
                    if (read == 0)
                    {
                        // 连接已关闭
                        throw new IOException("连接已关闭，无法读取完整数据");
                    }
                    totalBytesRead += read;
                }
                    
                Array.Copy(remainingBuffer, 0, result, 1, totalBytesRead);
                return result.Take(totalBytesRead + 1).ToArray();
            }
            else
            {
                // 有可用数据，直接读取
                var available = client.Available;
                var buffer = new byte[available];
                var totalBytesRead = 0;
                
                while (totalBytesRead < available)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(totalBytesRead, available - totalBytesRead));
                    if (read == 0)
                    {
                        // 连接已关闭
                        throw new IOException("连接已关闭，无法读取完整数据");
                    }
                    totalBytesRead += read;
                }
                
                return buffer.Take(totalBytesRead).ToArray();
            }
        }
        finally
        {
            _receiveLock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            // 停止所有TCP模块的监听，并触发断开连接事件
            foreach (var (config, _) in _tcpModuleClients)
            {
                StopListeningTcpModuleAsync(config).Wait();
                OnTcpModuleConnectionChanged(config, false);
            }
            _tcpModuleListeningTasks.Clear();
            _tcpModuleListeningCts.Clear();
            
            // 停止监听
            StopListeningTriggerPhotoelectric();
            
            // 释放锁
            _sendLock.Dispose();
            _receiveLock.Dispose();
            
            // 关闭连接
            TriggerPhotoelectricClient?.Close();
            foreach (var client in _tcpModuleClients.Values)
            {
                client.Close();
            }
            _tcpModuleClients.Clear();
        }

        _disposed = true;
    }

    /// <summary>
    /// 触发TCP模块连接状态改变事件
    /// </summary>
    /// <param name="config">TCP模块配置</param>
    /// <param name="connected">是否已连接</param>
    private void OnTcpModuleConnectionChanged(TcpConnectionConfig config, bool connected)
    {
        TcpModuleConnectionChanged?.Invoke(this, (config, connected));
        Log.Information("TCP模块 {Config} 连接状态改变: {Status}", config.IpAddress, connected ? "已连接" : "已断开");
    }
} 