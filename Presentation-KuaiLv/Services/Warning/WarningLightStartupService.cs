using Serilog;

namespace Presentation_KuaiLv.Services.Warning;

/// <summary>
/// 警示灯托管服务
/// </summary>
public class WarningLightStartupService
{
    private readonly IWarningLightService _warningLightService;

    public WarningLightStartupService(IWarningLightService warningLightService)
    {
        _warningLightService = warningLightService;
    }

    /// <summary>
    /// 启动服务
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _warningLightService.ConnectAsync();
            if (_warningLightService.IsConnected)
            {
                await _warningLightService.ShowGreenLightAsync();
            }
            Log.Information("警示灯托管服务启动成功");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动警示灯托管服务时发生错误");
        }
    }

    /// <summary>
    /// 停止服务
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_warningLightService.IsConnected)
            {
                await _warningLightService.TurnOffAllLightsAsync();
                await _warningLightService.DisconnectAsync();
            }
            Log.Information("警示灯托管服务已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止警示灯托管服务时发生错误");
        }
    }
} 