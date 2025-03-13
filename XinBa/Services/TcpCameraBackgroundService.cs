using Microsoft.Extensions.Hosting;
using Serilog;

namespace Presentation_XinBa.Services;

/// <summary>
///     TCP相机后台服务
/// </summary>
public class TcpCameraBackgroundService : BackgroundService
{
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly TcpCameraService _tcpCameraService;
    private IDisposable? _packageSubscription;
    private CancellationTokenSource? _stoppingTokenSource;

    /// <summary>
    ///     构造函数
    /// </summary>
    /// <param name="tcpCameraService">TCP相机服务</param>
    public TcpCameraBackgroundService(TcpCameraService tcpCameraService)
    {
        _tcpCameraService = tcpCameraService;
    }

    /// <summary>
    ///     是否已启动
    /// </summary>
    private bool IsStarted { get; set; }

    /// <summary>
    ///     启动服务
    /// </summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (IsStarted) return;

            Log.Information("正在启动TCP相机后台服务...");

            // 创建取消令牌源
            _stoppingTokenSource = new CancellationTokenSource();

            // 启动相机服务
            await _tcpCameraService.StartAsync();

            // 启动后台任务
            _ = ExecuteAsync(_stoppingTokenSource.Token);

            IsStarted = true;
            Log.Information("TCP相机后台服务已启动");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动TCP相机后台服务时发生错误");
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    ///     停止服务
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (!IsStarted) return;

            Log.Information("正在停止TCP相机后台服务...");

            // 取消后台任务
            _stoppingTokenSource?.Cancel();

            // 取消订阅
            _packageSubscription?.Dispose();
            _packageSubscription = null;

            // 停止相机服务
            await _tcpCameraService.StopAsync();

            IsStarted = false;
            Log.Information("TCP相机后台服务已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止TCP相机后台服务时发生错误");
        }
        finally
        {
            _initLock.Release();
            await base.StopAsync(cancellationToken);
        }
    }

    /// <summary>
    ///     执行后台任务
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // 保持服务运行，直到取消令牌被触发
            while (!stoppingToken.IsCancellationRequested)
            {
                // 只记录连接状态，不再尝试重新连接
                // 避免与TcpCameraService中的自动重连机制冲突
                if (!_tcpCameraService.IsConnected) Log.Warning("相机连接已断开，等待自动重连...");

                await Task.Delay(5000, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (Exception ex)
        {
            Log.Error(ex, "TCP相机后台服务执行时发生错误");
        }
    }
}