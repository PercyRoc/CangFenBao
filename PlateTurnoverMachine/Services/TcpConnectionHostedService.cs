using Common.Services.Settings;
using DongtaiFlippingBoardMachine.Models;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace DongtaiFlippingBoardMachine.Services;

/// <summary>
///     TCP连接托管服务
/// </summary>
internal class TcpConnectionHostedService(
    ITcpConnectionService tcpConnectionService,
    ISettingsService settingsService)
    : BackgroundService
{
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(5));
    private bool _isStopping;
    private PlateTurnoverSettings? _settings;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.Information("TCP连接托管服务启动");

        try
        {
            // 加载配置
            _settings = settingsService.LoadSettings<PlateTurnoverSettings>();

            while (await _timer.WaitForNextTickAsync(stoppingToken))
            {
                // 每次循环重新加载配置，确保使用最新配置
                _settings = settingsService.LoadSettings<PlateTurnoverSettings>();
                await ConnectDevicesAsync();
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("TCP连接托管服务停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "TCP连接托管服务执行时发生错误");
        }
    }

    private async Task ConnectDevicesAsync()
    {
        if (_isStopping)
        {
            Log.Debug("ConnectDevicesAsync: Service is stopping, skipping connection attempts.");
            return;
        }

        try
        {
            if (_settings == null)
            {
                Log.Warning("未能加载翻板机配置，跳过设备连接");
                return;
            }

            // 连接触发光电
            var triggerConfig =
                new TcpConnectionConfig(_settings.TriggerPhotoelectricIp, _settings.TriggerPhotoelectricPort);
            if (tcpConnectionService.TriggerPhotoelectricClient?.Connected != true)
                await tcpConnectionService.ConnectTriggerPhotoelectricAsync(triggerConfig);

            // 连接TCP模块
            var tcpConfigs = _settings.Items
                .Where(static item => !string.IsNullOrEmpty(item.TcpAddress))
                .Select(static item =>
                {
                    var parts = item.TcpAddress!.Split(':');
                    return new TcpConnectionConfig(
                        parts[0],
                        parts.Length > 1 && int.TryParse(parts[1], out var port) ? port : 2000
                    );
                })
                .Distinct()
                .ToList();

            // 每次都传递完整的配置列表，由TcpConnectionService内部处理按需连接
            await tcpConnectionService.ConnectTcpModulesAsync(tcpConfigs);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "连接设备时发生错误");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _isStopping = true;
        Log.Information("TCP连接托管服务正在停止... (停止标志已设置)");

        // 1. 停止定时器，阻止新的连接尝试
        _timer.Dispose();
        Log.Debug("连接检查定时器已释放");

        // 2. 调用基类的StopAsync，它会负责取消ExecuteAsync中的CancellationToken
        //    并等待ExecuteAsync方法结束（或超时）
        try
        {
            await base.StopAsync(cancellationToken);
            Log.Information("后台服务ExecuteAsync已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "等待后台服务ExecuteAsync停止时发生错误");
        }

        // 3. 在ExecuteAsync完全停止后，再安全地释放TCP连接服务
        try
        {
            if (tcpConnectionService is IDisposable disposable)
            {
                Log.Information("正在释放TCP连接服务资源...");
                disposable.Dispose(); // 这将关闭所有连接并清理资源
                Log.Information("TCP连接服务资源已释放");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "释放TCP连接服务时发生错误");
        }

        Log.Information("TCP连接托管服务已完全停止");
    }
}