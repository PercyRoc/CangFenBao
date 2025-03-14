using Common.Services.Settings;
using Microsoft.Extensions.Hosting;
using PlateTurnoverMachine.Models;
using Serilog;

namespace PlateTurnoverMachine.Services;

/// <summary>
///     TCP连接托管服务
/// </summary>
internal class TcpConnectionHostedService(
    ITcpConnectionService tcpConnectionService,
    ISettingsService settingsService)
    : BackgroundService
{
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(5));
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
                .ToList();

            var disconnectedConfigs = tcpConfigs
                .Where(config => !tcpConnectionService.TcpModuleClients.ContainsKey(config) ||
                                 !tcpConnectionService.TcpModuleClients[config].Connected)
                .ToList();

            if (disconnectedConfigs.Count != 0) await tcpConnectionService.ConnectTcpModulesAsync(disconnectedConfigs);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "连接设备时发生错误");
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        Log.Information("TCP连接托管服务正在停止");
        _timer.Dispose();
        return base.StopAsync(cancellationToken);
    }
}