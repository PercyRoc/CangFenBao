using Prism.Ioc;
using SortingService.Interfaces;
using SortingService.Services;

namespace SortingService.Extensions;

public static class ContainerRegistryExtensions
{
    /// <summary>
    ///     添加摆轮分拣服务
    /// </summary>
    public static IContainerRegistry AddSortingServices(this IContainerRegistry containerRegistry)
    {
        // 注册 TCP 客户端服务
        containerRegistry.Register<ITcpClientService, TcpClientService>();

        // 注册摆轮分拣服务
        containerRegistry.RegisterSingleton<IPendulumSortService, PendulumSortService>();

        return containerRegistry;
    }
}