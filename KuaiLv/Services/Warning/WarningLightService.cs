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
internal class WarningLightService : IWarningLightService, IDisposable
{
    private readonly INotificationService _notificationService;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private WarningLightConfiguration _currentConfig;
    private bool _disposed;
    private bool _isConnected;
    private NetworkStream? _networkStream;
    private TcpClient? _tcpClient;

    public WarningLightService(
        ISettingsService settingsService,
        INotificationService notificationService)
    {
        _notificationService = notificationService;
        _currentConfig = settingsService.LoadSettings<WarningLightConfiguration>();

        // 订阅配置变更事件
        settingsService.OnSettingsChanged<WarningLightConfiguration>(OnConfigurationChanged);
    }

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
        }
    }

    /// <inheritdoc />
    public async Task ConnectAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            Log.Information("开始连接警示灯...");
            Log.Information("当前配置 - IP地址: {IpAddress}, 端口: {Port}, 是否启用: {IsEnabled}",
                _currentConfig.IpAddress, _currentConfig.Port, _currentConfig.IsEnabled);

            if (IsConnected)
            {
                Log.Information("警示灯已经处于连接状态，正在断开重连...");
                await DisconnectAsync();
            }

            if (!_currentConfig.IsEnabled)
            {
                Log.Information("警示灯未启用，跳过连接");
                IsConnected = false;
                return;
            }

            Log.Information("正在创建TCP连接...");
            _tcpClient = new TcpClient();

            try
            {
                var connectTask = _tcpClient.ConnectAsync(_currentConfig.IpAddress, _currentConfig.Port);
                if (await Task.WhenAny(connectTask, Task.Delay(_currentConfig.ConnectionTimeout)) != connectTask)
                    throw new TimeoutException($"连接超时（{_currentConfig.ConnectionTimeout}ms）");

                await connectTask;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "TCP连接失败 - IP: {IpAddress}, 端口: {Port}",
                    _currentConfig.IpAddress, _currentConfig.Port);
                throw;
            }

            Log.Information("TCP连接成功，正在获取网络流...");
            _networkStream = _tcpClient.GetStream();

            IsConnected = true;
            Log.Information("警示灯连接成功");
            _notificationService.ShowSuccess("警示灯连接成功");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "警示灯连接失败");
            _notificationService.ShowError($"警示灯连接失败: {ex.Message}");
            await DisconnectAsync();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task DisconnectAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            Log.Information("开始断开警示灯连接...");

            if (_networkStream != null)
            {
                Log.Information("正在关闭网络流...");
                await _networkStream.DisposeAsync();
                _networkStream = null;
            }

            if (_tcpClient != null)
            {
                Log.Information("正在关闭TCP连接...");
                _tcpClient.Dispose();
                _tcpClient = null;
            }

            IsConnected = false;
            Log.Information("警示灯断开连接完成");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "警示灯断开连接失败");
        }
        finally
        {
            _semaphore.Release();
        }
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

    /// <summary>
    ///     处理配置变更事件
    /// </summary>
    private void OnConfigurationChanged(WarningLightConfiguration newConfig)
    {
        Log.Information("警示灯配置已更新");
        _currentConfig = newConfig;

        // 如果配置发生变化，重新连接服务
        if (!IsConnected) return;

        Log.Information("正在重新连接警示灯服务...");
        _ = Task.Run(async () =>
        {
            try
            {
                await ConnectAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "重新连接警示灯服务失败");
            }
        });
    }

    private async Task SendCommandAsync(byte[] command)
    {
        if (command.Length == 0) return;

        var commandText = Encoding.UTF8.GetString(command).Trim();

        await _semaphore.WaitAsync();
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
            _notificationService.ShowError("警示灯控制失败");
            await DisconnectAsync();
            throw; // 重新抛出异常，让调用者知道发生了错误
        }
        finally
        {
            _semaphore.Release();
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _semaphore.Dispose();
            DisconnectAsync().Wait();
        }

        _disposed = true;
    }
}