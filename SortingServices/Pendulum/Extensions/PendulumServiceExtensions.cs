using Common.Models.Settings.Sort.PendulumSort;

namespace SortingServices.Pendulum.Extensions;

/// <summary>
///     摆轮分拣服务扩展方法
/// </summary>
public static class PendulumServiceExtensions
{
    /// <summary>
    ///     注册指定类型的摆轮分拣服务
    /// </summary>
    /// <param name="containerRegistry">容器注册器</param>
    /// <param name="serviceType">服务类型</param>
    public static void RegisterPendulumSortService(
        this IContainerRegistry containerRegistry,
        PendulumServiceType serviceType)
    {
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