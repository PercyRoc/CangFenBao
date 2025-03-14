using System.IO;
using Common.Data;
using Common.Services.Audio;
using Common.Services.Settings;
using Common.Services.Ui;
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
            .UseSqlite($"Data Source={dbPath}", sqliteOptions =>
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

        // 注册对话框服务
        services.RegisterSingleton<IDialogService, DialogService>();

        // 注册通知服务
        services.RegisterSingleton<INotificationService, NotificationService>();
    }
}