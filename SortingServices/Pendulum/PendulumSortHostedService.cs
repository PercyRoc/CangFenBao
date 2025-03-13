using Common.Services.Settings;
using Microsoft.Extensions.Hosting;
using Serilog;
using SortingServices.Pendulum.Models;

namespace SortingServices.Pendulum;

/// <summary>
/// 摆轮分拣服务托管服务
/// </summary>
public class PendulumSortHostedService : IHostedService
{
    private readonly IPendulumSortService _pendulumSortService;
    private readonly ISettingsService _settingsService;

    public PendulumSortHostedService(
        IPendulumSortService pendulumSortService,
        ISettingsService settingsService)
    {
        _pendulumSortService = pendulumSortService;
        _settingsService = settingsService;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 获取配置
            var config = _settingsService.LoadSettings<PendulumSortConfig>();

            // 初始化服务
            await _pendulumSortService.InitializeAsync(config);

            // 启动服务
            await _pendulumSortService.StartAsync();
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
            await _pendulumSortService.StopAsync();
            Log.Information("摆轮分拣服务已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止摆轮分拣服务时发生错误");
            throw;
        }
    }
} 