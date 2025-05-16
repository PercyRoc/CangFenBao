using System.IO;
using Common.Data;
using Common.Services.Audio;
using Common.Services.License;
using Common.Services.Settings;
using Common.Services.Ui;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Common.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     添加通用服务
    /// </summary>
    public static IContainerRegistry AddCommonServices(this IContainerRegistry containerRegistry)
    {
        // 注册设置服务
        var settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings");
        containerRegistry.RegisterSingleton<ISettingsService>(() => new SettingsService(settingsPath));

        // 注册UI通知服务
        containerRegistry.RegisterSingleton<INotificationService, NotificationService>();

        // 注册音频服务
        containerRegistry.RegisterSingleton<IAudioService, AudioService>();

        // 注册数据库上下文
        var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "packages.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        // 创建数据库上下文选项
        var options = new DbContextOptionsBuilder<PackageDbContext>()
            .UseSqlite($"Data Source={dbPath}", static sqliteOptions =>
            {
                sqliteOptions.CommandTimeout(30); // 设置命令超时时间（秒）
            })
            .Options;

        // 注册数据库上下文选项
        containerRegistry.RegisterInstance(options);
        // 注册数据服务
        containerRegistry.RegisterSingleton<IPackageDataService, PackageDataService>();

        return containerRegistry;
    }

    /// <summary>
    ///     添加授权验证服务
    /// </summary>
    public static void AddLicenseService(this IContainerRegistry containerRegistry)
    {
        // 注册授权服务
        containerRegistry.RegisterSingleton<ILicenseService, LicenseService>();
    }
}