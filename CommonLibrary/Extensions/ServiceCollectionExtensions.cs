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

        // 数据库配置
        var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "packages.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        // 初始化核心表结构
        var options = new DbContextOptionsBuilder<PackageDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        using (var context = new PackageDbContext(options))
        {
            context.Database.EnsureCreated();
        }

        // 注册数据服务
        services.RegisterSingleton<IPackageDataService, PackageDataService>();

        // 注册音频服务
        services.RegisterSingleton<IAudioService, AudioService>();

        return services;
    }
}