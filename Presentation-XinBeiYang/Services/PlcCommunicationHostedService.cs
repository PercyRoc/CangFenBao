using Microsoft.Extensions.Hosting;
using CommonLibrary.Services;
using Presentation_XinBeiYang.Models;
using Serilog;

namespace Presentation_XinBeiYang.Services;

/// <summary>
/// PLC通讯托管服务
/// </summary>
public class PlcCommunicationHostedService(
    IPlcCommunicationService plcCommunicationService,
    ISettingsService settingsService)
    : BackgroundService
{
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(5));
    private HostConfiguration? _configuration;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.Information("PLC通讯托管服务启动");

        try
        {
            // 加载配置
            _configuration = settingsService.LoadConfiguration<HostConfiguration>();
            
            while (await _timer.WaitForNextTickAsync(stoppingToken))
            {
                // 每次循环重新加载配置，确保使用最新配置
                _configuration = settingsService.LoadConfiguration<HostConfiguration>();

                // 如果未连接，尝试连接
                if (!plcCommunicationService.IsConnected)
                {
                    await plcCommunicationService.ConnectAsync(
                        _configuration.IpAddress,
                        _configuration.Port);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("PLC通讯托管服务停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PLC通讯托管服务执行时发生错误");
        }
        finally
        {
            // 确保在服务停止时断开连接
            if (plcCommunicationService.IsConnected)
            {
                await plcCommunicationService.DisconnectAsync();
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Log.Information("正在停止PLC通讯托管服务");

        try
        {
            // 断开PLC连接
            if (plcCommunicationService.IsConnected)
            {
                await plcCommunicationService.DisconnectAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止PLC通讯托管服务时发生错误");
        }
        finally
        {
            await base.StopAsync(cancellationToken);
        }
    }
} 