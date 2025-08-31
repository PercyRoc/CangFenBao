using System.Net.Sockets;
using System.Text;
using Common.Services.Settings;
using Common.Services.Ui;
using KuaiLv.Models.Settings.Warning;
using Serilog;

namespace KuaiLv.Services.Warning;

/// <summary>
///     警示灯服务实现
/// </summary>
public class WarningLightService(
    ISettingsService settingsService,
    INotificationService notificationService)
    : IWarningLightService, IDisposable
{
    private bool _disposed;
    private bool _isConnected;
    private NetworkStream? _networkStream;
    private TcpClient? _tcpClient;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public event Action<bool>? ConnectionChanged;

    /// <inheritdoc />
    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (_isConnected == value) return;

            _isConnected = value;
            ConnectionChanged?.Invoke(value);

            // 连接状态改变时记录日志
            if (value)
                Log.Information("警示灯连接已建立");
            else
                Log.Warning("警示灯连接已断开");
        }
    }

    /// <inheritdoc />
    public async Task ConnectAsync()
    {
        try
        {
            Log.Information("开始连接警示灯...");
            var currentConfig = settingsService.LoadSettings<WarningLightConfiguration>();
            Log.Information("当前配置 - IP地址: {IpAddress}, 端口: {Port}, 是否启用: {IsEnabled}",
                currentConfig.IpAddress, currentConfig.Port, currentConfig.IsEnabled);

            if (IsConnected)
            {
                Log.Information("警示灯已经处于连接状态，正在断开重连...");
                await DisconnectAsync();
            }

            if (!currentConfig.IsEnabled)
            {
                Log.Information("警示灯未启用，跳过连接");
                IsConnected = false;
                return;
            }

            Log.Information("正在创建TCP连接...");
            _tcpClient = new TcpClient();

            try
            {
                var connectTask = _tcpClient.ConnectAsync(currentConfig.IpAddress, currentConfig.Port);
                if (await Task.WhenAny(connectTask, Task.Delay(currentConfig.ConnectionTimeout)) != connectTask)
                    throw new TimeoutException($"连接超时（{currentConfig.ConnectionTimeout}ms）");

                await connectTask;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "TCP连接失败 - IP: {IpAddress}, 端口: {Port}",
                    currentConfig.IpAddress, currentConfig.Port);
                throw;
            }

            Log.Information("TCP连接成功，正在获取网络流...");
            _networkStream = _tcpClient.GetStream();

            IsConnected = true;
            Log.Information("警示灯连接成功");
            notificationService.ShowSuccess("警示灯连接成功");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "警示灯连接失败");
            notificationService.ShowError($"警示灯连接失败: {ex.Message}");
            await DisconnectAsync();
            throw; // 重新抛出异常，让调用者知道发生了错误
        }
    }

    /// <inheritdoc />
    public async Task DisconnectAsync()
    {
        // Prevent multiple simultaneous disconnections if needed (using a simple flag or SemaphoreSlim)
        // For now, assume external coordination or low risk of concurrent calls.

        Log.Information("开始断开警示灯连接 (IsConnected: {InitialState})...", _isConnected);
        // 如果已经断开或者内部对象已为null，则可能正在断开或已完成，直接返回
        if (!_isConnected && _tcpClient == null && _networkStream == null)
        {
            Log.Debug("警示灯似乎已断开或正在断开，跳过重复操作");
            return;
        }

        var wasConnected = IsConnected; // Record state before changing

        // 1. 立即更新状态并清除内部引用，防止新命令使用旧资源
        //    ConnectionChanged 事件会在这里触发
        IsConnected = false;
        var streamToDispose = _networkStream;
        var clientToDispose = _tcpClient;
        _networkStream = null; // Nullify references early
        _tcpClient = null;

        try
        {
            // 2. 异步释放网络流
            if (streamToDispose != null)
            {
                Log.Debug("正在异步释放网络流...");
                try
                {
                    // 注意: DisposeAsync 本身可能阻塞，但通常优于在异步方法中调用同步 Dispose
                    await streamToDispose.DisposeAsync();
                    Log.Debug("网络流已异步释放");
                }
                catch (ObjectDisposedException)
                {
                    Log.Debug("网络流已被释放");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "异步释放网络流期间发生错误");
                    // 继续尝试释放客户端
                }
            }
            else
            {
                Log.Debug("网络流实例为 null，跳过释放");
            }

            // 3. 释放 TCP 客户端 (Dispose 应处理关闭连接)
            if (clientToDispose != null)
            {
                Log.Debug("正在释放 TCP 客户端...");
                try
                {
                    clientToDispose.Dispose(); // 同步释放，Dispose 应该关闭连接
                    Log.Debug("TCP 客户端已释放");
                }
                catch (ObjectDisposedException)
                {
                    Log.Debug("TCP 客户端已被释放");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "释放 TCP 客户端期间发生错误");
                }
            }
            else
            {
                Log.Debug("TCP 客户端实例为 null，跳过释放");
            }

            if (wasConnected)
                Log.Information("警示灯断开连接流程完成");
            else
                Log.Debug("警示灯断开连接处理完成 (初始状态或已为断开)");
        }
        catch (Exception ex)
        {
            // 捕获整个过程中的意外错误
            Log.Error(ex, "警示灯断开连接期间发生意外错误");
            // 状态已经设置为断开
        }
        // Final state is IsConnected = false, _tcpClient = null, _networkStream = null
    }

    /// <inheritdoc />
    public Task ShowGreenLightAsync()
    {
        // 绿灯指令：AT+STACH1=1
        var command = "AT+STACH3=1\r\n"u8.ToArray();
        return SendCommandAsync(command);
    }

    /// <inheritdoc />
    public Task ShowRedLightAsync()
    {
        // 红灯指令：AT+STACH2=1
        var command = "AT+STACH2=1\r\n"u8.ToArray();
        return SendCommandAsync(command);
    }

    /// <inheritdoc />
    public Task TurnOffGreenLightAsync()
    {
        var command = "AT+STACH3=0\r\n"u8.ToArray();
        return SendCommandAsync(command);
    }

    /// <inheritdoc />
    public Task TurnOffRedLightAsync()
    {
        var command = "AT+STACH2=0\r\n"u8.ToArray();
        return SendCommandAsync(command);
    }

    private async Task SendCommandAsync(byte[] command)
    {
        if (command.Length == 0) return;

        var commandText = Encoding.UTF8.GetString(command).Trim();

        try
        {
            // 检查连接状态
            if (_tcpClient == null || _networkStream == null || !_tcpClient.Connected || !IsConnected)
            {
                Log.Warning("警示灯未连接或连接已断开，尝试重新连接");
                await ConnectAsync();

                if (!IsConnected || _networkStream == null)
                {
                    Log.Warning("警示灯连接失败，无法发送命令: {Command}", commandText);
                    return;
                }
            }

            await _networkStream!.WriteAsync(command);
            await _networkStream.FlushAsync();
            // 添加小延时确保命令被完全处理
            await Task.Delay(50);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "发送警示灯指令失败: {Command}", commandText);
            notificationService.ShowError("警示灯控制失败");
            await DisconnectAsync();
            throw; // 重新抛出异常，让调用者知道发生了错误
        }
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            Log.Information("Disposing WarningLightService (同步)...");

            // 同步释放拥有的资源，以防 DisconnectAsync 未被调用或未完全执行
            // 直接检查实例字段，因为 DisconnectAsync 会将其设为 null
            var currentStream = _networkStream;
            var currentClient = _tcpClient;

            // 在 Dispose 之前立即将字段设为 null，减少竞争条件窗口
            _networkStream = null;
            _tcpClient = null;
            // 确保状态反映 Dispose
            if (_isConnected) // 仅在需要时记录状态更改日志
            {
                IsConnected = false; // 这会触发事件，确保事件处理器是安全的
                Log.Debug("Dispose: 将 IsConnected 设为 false");
            }


            try
            {
                // 先释放流，再释放客户端
                if (currentStream != null)
                {
                    Log.Debug("Dispose: 同步释放 NetworkStream...");
                    try
                    {
                        currentStream.Dispose();
                    }
                    catch (ObjectDisposedException)
                    {
                        Log.Debug("Dispose: NetworkStream 已被释放.");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Dispose: 同步释放 NetworkStream 时出错.");
                    }
                }

                if (currentClient != null)
                {
                    Log.Debug("Dispose: 同步释放 TcpClient...");
                    try
                    {
                        currentClient.Dispose();
                    }
                    catch (ObjectDisposedException)
                    {
                        Log.Debug("Dispose: TcpClient 已被释放.");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Dispose: 同步释放 TcpClient 时出错.");
                    }
                }

                Log.Information("同步 Dispose 完成资源清理尝试.");
            }
            catch (Exception ex)
            {
                // 捕获 Dispose 逻辑本身的错误
                Log.Error(ex, "同步 Dispose 期间发生意外错误.");
            }
            // 取消订阅事件，如果在这里订阅的话
            // settingsService.OnSettingsChanged -= OnConfigurationChanged; // 需要实际的取消订阅逻辑
        }

        _disposed = true;
        Log.Information("WarningLightService disposed 标志已设置.");
    }
}