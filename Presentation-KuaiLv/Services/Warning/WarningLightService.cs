using System.Net.Sockets;
using System.Text;
using CommonLibrary.Services;
using Presentation_CommonLibrary.Services;
using Presentation_KuaiLv.Models.Settings.Warning;
using Serilog;

namespace Presentation_KuaiLv.Services.Warning;

/// <summary>
///     警示灯服务实现
/// </summary>
public class WarningLightService(
    ISettingsService settingsService,
    INotificationService notificationService)
    : IWarningLightService, IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
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
        }
    }

    /// <inheritdoc />
    public async Task ConnectAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            if (IsConnected) await DisconnectAsync();

            var config = settingsService.LoadConfiguration<WarningLightConfiguration>();
            if (!config.IsEnabled)
            {
                Log.Information("警示灯未启用");
                IsConnected = false;
                return;
            }

            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(config.IpAddress, config.Port);
            _networkStream = _tcpClient.GetStream();

            IsConnected = true;
            Log.Information("警示灯连接成功");
            notificationService.ShowSuccess("警示灯连接成功", "已连接到警示灯控制器");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "警示灯连接失败");
            notificationService.ShowError("警示灯连接失败", ex.Message);
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
            if (_networkStream != null)
            {
                await _networkStream.DisposeAsync();
                _networkStream = null;
            }

            if (_tcpClient != null)
            {
                _tcpClient.Dispose();
                _tcpClient = null;
            }

            IsConnected = false;
            Log.Information("警示灯断开连接");
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
    public async Task ShowGreenLightAsync()
    {
        // 绿灯指令：AT+STACH1=1
        var command = "AT+STACH3=1\r\n"u8.ToArray();
        await SendCommandAsync(command);
    }

    /// <inheritdoc />
    public async Task ShowRedLightAsync()
    {
        // 红灯指令：AT+STACH2=1
        var command = "AT+STACH2=1\r\n"u8.ToArray();
        await SendCommandAsync(command);
    }
    
    /// <inheritdoc />
    public async Task TurnOffGreenLightAsync()
    {
        var command = "AT+STACH3=0\r\n"u8.ToArray();
        await SendCommandAsync(command);
    }
    
    /// <inheritdoc />
    public async Task TurnOffRedLightAsync()
    {
        var command = "AT+STACH2=0\r\n"u8.ToArray();
        await SendCommandAsync(command);
    }

    private async Task SendCommandAsync(byte[] command)
    {
        if (command.Length == 0)
        {
            return;
        }
        
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
            notificationService.ShowError("警示灯控制失败", ex.Message);
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