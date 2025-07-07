using Common.Services.Audio;
using Common.Services.Notifications;
using Common.Services.Settings;
using Common.ViewModels.Settings.ChuteRules;
using Common.Views.Settings.ChuteRules;
using Serilog;

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
        try
        {
            var settingsService = containerProvider.Resolve<ISettingsService>();
            await settingsService.WaitForInitializationAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CommonServicesModule] OnInitialized 期间解析或等待 ISettingsService 时发生错误.");
        }
    }

    /// <summary>
    /// 用于向容器注册类型。
    /// </summary>
    /// <param name="containerRegistry">容器注册表。</param>
    public void RegisterTypes(IContainerRegistry containerRegistry)
    {
        try
        {
            // 注册设置服务为单例
            containerRegistry.RegisterSingleton<ISettingsService, SettingsService>();

            // 注册音频服务为单例
            containerRegistry.RegisterSingleton<IAudioService, AudioService>();

            // 注册通知服务为单例
            containerRegistry.RegisterSingleton<INotificationService, NotificationService>();

            containerRegistry.RegisterForNavigation<ChuteRuleSettingsView, ChuteRuleSettingsViewModel>();

            containerRegistry.RegisterDialogWindow<CustomDialogWindow>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CommonServicesModule] RegisterTypes 期间发生错误.");
            throw;
        }
    }
} 