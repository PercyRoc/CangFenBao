using Common.Models.Settings.Sort.PendulumSort;
using Common.Services.Settings;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace SortingServices.Pendulum;

/// <summary>
///     摆轮分拣服务托管服务
/// </summary>
public class PendulumSortHostedService(
    IPendulumSortService pendulumSortService,
    ISettingsService settingsService)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 获取配置
            var config = settingsService.LoadSettings<PendulumSortConfig>();

            // 初始化服务
            await pendulumSortService.InitializeAsync(config);

            // 启动服务
            await pendulumSortService.StartAsync();
            Log.Information("摆轮分拣服务已启动");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动摆轮分拣服务时发生错误");
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await pendulumSortService.StopAsync();
            Log.Information("摆轮分拣服务已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止摆轮分拣服务时发生错误");
            throw;
        }
    }
}