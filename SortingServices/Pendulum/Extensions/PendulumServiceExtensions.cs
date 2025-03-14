using Common.Services.Settings;
using Prism.Ioc;
using SortingServices.Pendulum.Models;

namespace SortingServices.Pendulum.Extensions;

/// <summary>
///     摆轮分拣服务扩展方法
/// </summary>
public static class PendulumServiceExtensions
{
    /// <summary>
    ///     注册摆轮分拣服务
    /// </summary>
    /// <param name="containerRegistry">容器注册器</param>
    /// <param name="settingsService">设置服务</param>
    public static void RegisterPendulumSortService(
        this IContainerRegistry containerRegistry,
        ISettingsService settingsService)
    {
        // 加载配置
        var pendulumConfig = settingsService.LoadSettings<PendulumSortConfig>();
        containerRegistry.RegisterInstance(pendulumConfig);

        // 注册配置变更处理
        settingsService.OnSettingsChanged<PendulumSortConfig>(config =>
        {
            // 更新容器中的配置实例
            containerRegistry.RegisterInstance(config);
        });

        // 根据配置选择合适的摆轮服务实现
        if (pendulumConfig.SortingPhotoelectrics.Count > 0)
            // 多光电多摆轮
            containerRegistry.RegisterSingleton<IPendulumSortService, MultiPendulumSortService>();
        else
            // 单光电单摆轮
            containerRegistry.RegisterSingleton<IPendulumSortService, SinglePendulumSortService>();
    }

    /// <summary>
    ///     注册指定类型的摆轮分拣服务
    /// </summary>
    /// <param name="containerRegistry">容器注册器</param>
    /// <param name="settingsService">设置服务</param>
    /// <param name="serviceType">服务类型</param>
    public static void RegisterPendulumSortService(
        this IContainerRegistry containerRegistry,
        ISettingsService settingsService,
        PendulumServiceType serviceType)
    {
        // 加载配置
        var pendulumConfig = settingsService.LoadSettings<PendulumSortConfig>();
        containerRegistry.RegisterInstance(pendulumConfig);

        // 注册配置变更处理
        settingsService.OnSettingsChanged<PendulumSortConfig>(config =>
        {
            // 更新容器中的配置实例
            containerRegistry.RegisterInstance(config);
        });

        // 根据指定类型注册服务
        switch (serviceType)
        {
            case PendulumServiceType.Single:
                containerRegistry.RegisterSingleton<IPendulumSortService, SinglePendulumSortService>();
                break;
            case PendulumServiceType.Multi:
                containerRegistry.RegisterSingleton<IPendulumSortService, MultiPendulumSortService>();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(serviceType), serviceType, "不支持的摆轮服务类型");
        }
    }
}