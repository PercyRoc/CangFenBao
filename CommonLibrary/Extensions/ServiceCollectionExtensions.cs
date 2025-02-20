using System.IO;
using CommonLibrary.Data;
using CommonLibrary.Services;
using Microsoft.EntityFrameworkCore;
using Prism.Ioc;

namespace CommonLibrary.Extensions;

public static class ServiceCollectionExtensions
{
    public static IContainerRegistry AddCommonServices(this IContainerRegistry services)
    {
        // 注册设置服务
        services.RegisterSingleton<ISettingsService>(() =>
            new JsonSettingsService(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings")));

        // 注册包裹数据服务
        var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "packages.db");
        var connectionString = $"Data Source={dbPath}";
        
        // 确保数据目录存在
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        
        // 注册数据服务
        services.RegisterSingleton<IPackageDataService, PackageDataService>();

        return services;
    }
}