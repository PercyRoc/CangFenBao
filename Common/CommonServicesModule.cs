using Common.Services.Audio;
using Common.Services.Settings;
using Common.Services.Ui;

namespace Common;

/// <summary>
/// Common服务模块，用于注册通用的服务。
/// </summary>
public class CommonServicesModule : IModule
{
    /// <summary>
    /// 当模块被初始化时调用。
    /// </summary>
    /// <param name="containerProvider">容器提供者。</param>
    public void OnInitialized(IContainerProvider containerProvider)
    {
        // 可选：在模块初始化后执行某些操作，例如解析服务并调用初始化方法。
        // var settingsService = containerProvider.Resolve<ISettingsService>();
        // var audioService = containerProvider.Resolve<IAudioService>();
        // var notificationService = containerProvider.Resolve<INotificationService>();
    }

    /// <summary>
    /// 用于向容器注册类型。
    /// </summary>
    /// <param name="containerRegistry">容器注册表。</param>
    public void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // 注册设置服务为单例
        containerRegistry.RegisterSingleton<ISettingsService, SettingsService>();

        // 注册音频服务为单例
        containerRegistry.RegisterSingleton<IAudioService, AudioService>();

        // 注册通知服务为单例
        containerRegistry.RegisterSingleton<INotificationService, NotificationService>();

        // 注意：LicenseService 也可以在这里注册，如果它是一个通用的基础服务
        // containerRegistry.RegisterSingleton<ILicenseService, LicenseService>();
    }
} 