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
    public async void OnInitialized(IContainerProvider containerProvider)
    {
        var settingsService = containerProvider.Resolve<ISettingsService>();
        await settingsService.WaitForInitializationAsync();
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
    }
} 