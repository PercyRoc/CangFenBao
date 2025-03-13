using Serilog;

namespace Presentation_KuaiLv.Services.Warning;

/// <summary>
///     警示灯托管服务
/// </summary>
public class WarningLightStartupService(IWarningLightService warningLightService)
{
    /// <summary>
    ///     启动服务
    /// </summary>
    public async Task StartAsync()
    {
        try
        {
            await warningLightService.ConnectAsync();
            if (warningLightService.IsConnected) await warningLightService.ShowGreenLightAsync();
            Log.Information("警示灯托管服务启动成功");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动警示灯托管服务时发生错误");
        }
    }

    /// <summary>
    ///     停止服务
    /// </summary>
    public async Task StopAsync()
    {
        Log.Information("开始停止警示灯托管服务...");

        try
        {
            if (warningLightService.IsConnected)
            {
                await warningLightService.TurnOffRedLightAsync();
                // 等待一段时间确保命令被处理
                await Task.Delay(200);
                await warningLightService.TurnOffGreenLightAsync();
                await warningLightService.DisconnectAsync();
            }

            Log.Information("警示灯托管服务已停止");
        }
        catch (Exception ex)
        {
            Log.Error($"停止警示灯托管服务发生错误{ex}");
        }
    }
}