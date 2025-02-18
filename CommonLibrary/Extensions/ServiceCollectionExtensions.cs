using System.IO;
using CommonLibrary.Services;
using Prism.Ioc;

namespace CommonLibrary.Extensions;

public static class ServiceCollectionExtensions
{
    public static IContainerRegistry AddCommonServices(this IContainerRegistry services)
    {
        // 注册设置服务
        services.RegisterSingleton<ISettingsService>(() =>
            new JsonSettingsService(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings")));

        return services;
    }
}