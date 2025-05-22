using BalanceSorting.Models;
using BalanceSorting.Service;
using Common.Services.Settings;

namespace BalanceSorting.Modules;

/// <summary>
/// 多光电多摆轮分拣 Prism 模块
/// </summary>
public class MultiPendulumSortModule : IModule
{
    public void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // 注册多摆轮分拣服务为单例
        containerRegistry.RegisterSingleton<IPendulumSortService, MultiPendulumSortService>();
    }

    public void OnInitialized(IContainerProvider containerProvider)
    {
        // 自动启动服务
        var service = containerProvider.Resolve<IPendulumSortService>();
        var settingsService = containerProvider.Resolve<ISettingsService>();
        var config = settingsService.LoadSettings<PendulumSortConfig>();
        service.InitializeAsync(config).Wait();
        service.StartAsync().Wait();
    }
} 