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
public class WarningLightService : IWarningLightService, IDisposable
{
    private readonly INotificationService _notificationService;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly ISettingsService _settingsService;
    private bool _disposed;
    private bool _isConnected;
    private NetworkStream? _networkStream;
    private TcpClient? _tcpClient;

    public WarningLightService(
        ISettingsService settingsService,
        INotificationService notificationService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;
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
            if (_isConnected != value)
            {
                _isConnected = value;
                ConnectionChanged?.Invoke(value);
            }
        }
    }

    /// <inheritdoc />
    public async Task ConnectAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            if (IsConnected) await DisconnectAsync();

            var config = _settingsService.LoadConfiguration<WarningLightConfiguration>();
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
            _notificationService.ShowSuccess("警示灯连接成功", "已连接到警示灯控制器");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "警示灯连接失败");
            _notificationService.ShowError("警示灯连接失败", ex.Message);
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
        var command = Encoding.ASCII.GetBytes("AT+STACH1=1\r\n");
        await SendCommandAsync(command);
    }

    /// <inheritdoc />
    public async Task ShowRedLightAsync()
    {
        // 红灯指令：AT+STACH2=1
        var command = Encoding.ASCII.GetBytes("AT+STACH2=1\r\n");
        await SendCommandAsync(command);
    }

    /// <inheritdoc />
    public async Task TurnOffAllLightsAsync()
    {
        // 关闭所有灯
        var commands = new[]
        {
            "AT+STACH1=0\r\n",
            "AT+STACH2=0\r\n"
        };

        foreach (var cmd in commands)
        {
            await SendCommandAsync(Encoding.ASCII.GetBytes(cmd));
            await Task.Delay(100); // 指令之间添加延时
        }
    }

    private async Task SendCommandAsync(byte[] command)
    {
        await _semaphore.WaitAsync();
        try
        {
            if (!IsConnected)
            {
                Log.Warning("警示灯未连接，尝试重新连接");
                await ConnectAsync();
                if (!IsConnected) return;
            }

            await _networkStream!.WriteAsync(command);
            await _networkStream.FlushAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "发送警示灯指令失败");
            _notificationService.ShowError("警示灯控制失败", ex.Message);
            await DisconnectAsync();
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