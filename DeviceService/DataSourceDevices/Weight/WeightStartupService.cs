using Common.Services.Settings;
using Common.Services.Ui;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace DeviceService.DataSourceDevices.Weight;

/// <summary>
///     重量称启动服务
/// </summary>
public class WeightStartupService : IHostedService
{
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;
    private SerialPortWeightService? _weightService;

    /// <summary>
    ///     构造函数
    /// </summary>
    public WeightStartupService(
        INotificationService notificationService,
        ISettingsService settingsService)
    {
        _notificationService = notificationService;
        _settingsService = settingsService;
    }

    /// <summary>
    ///     启动服务
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var weight = GetWeightService();
            if (!weight.Start())
            {
                const string message = "Failed to start weight scale service";
                Log.Warning(message);
                _notificationService.ShowError(message);
            }
            else
            {
                Log.Information("Weight scale service started successfully");
                _notificationService.ShowSuccess("Weight scale service started successfully");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动重量称服务时发生错误");
            _notificationService.ShowError(ex.Message);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///     停止服务
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            Log.Information("正在停止重量称服务...");
            if (_weightService != null)
            {
                // 使用超时机制确保不会无限等待
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    timeoutCts.Token, cancellationToken);

                try
                {
                    // 先停止服务
                    _weightService.Stop();
                    // 释放资源
                    _weightService.Dispose();
                    _weightService = null;
                }
                catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
                {
                    Log.Warning("停止重量称服务超时");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "停止重量称服务时发生错误");
                }
            }

            Log.Information("重量称服务已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止重量称服务时发生错误");
        }
        finally
        {
            _initLock.Dispose();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///     获取重量称服务实例
    /// </summary>
    public SerialPortWeightService GetWeightService()
    {
        _initLock.Wait();
        try
        {
            return _weightService ??= new SerialPortWeightService(_settingsService);
        }
        finally
        {
            _initLock.Release();
        }
    }
}