using System.IO;
using Common.Data;
using Common.Services.Audio;
using Common.Services.License;
using Common.Services.Settings;
using Common.Services.Ui;
using Common.Services.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Prism.Ioc;

namespace Common.Extensions;

public static class ServiceCollectionExtensions
{
    public static void AddCommonServices(this IContainerRegistry services)
    {
        // 注册设置服务
        var settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings");
        services.RegisterSingleton<ISettingsService>(() =>
        {
            // 创建一个临时的ServiceProvider用于初始化SettingsService
            var serviceCollection = new ServiceCollection();
            serviceCollection.BuildServiceProvider();
            return new SettingsService(settingsPath);
        });

        // 数据库配置
        var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "packages.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        // 创建数据库上下文选项
        var options = new DbContextOptionsBuilder<PackageDbContext>()
            .UseSqlite($"Data Source={dbPath}", static sqliteOptions =>
            {
                sqliteOptions.CommandTimeout(30); // 设置命令超时时间（秒）
            })
            .Options;

        // 初始化核心表结构
        using (var context = new PackageDbContext(options))
        {
            context.Database.EnsureCreated();
        }

        // 注册数据库上下文选项
        services.RegisterInstance(options);
        // 注册数据服务
        services.RegisterSingleton<IPackageDataService, PackageDataService>();
        // 注册音频服务
        services.RegisterSingleton<IAudioService, AudioService>();
        // 注册通知服务
        services.RegisterSingleton<INotificationService, NotificationService>();
        services.RegisterSingleton<ISettingsService, SettingsService>();
        // 注册授权服务
        services.RegisterSingleton<ILicenseService, LicenseService>();
        // 注册单号校验服务
        services.RegisterSingleton<IBarcodeValidationService, BarcodeValidationService>();
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