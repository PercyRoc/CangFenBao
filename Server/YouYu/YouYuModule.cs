using Server.YouYu.Services;
using Server.YouYu.ViewModels;
using Server.YouYu.Views;

namespace Server.YouYu
{
    public class YouYuModule : IModule
    {
        public void RegisterTypes(IContainerRegistry containerRegistry)
        {
            containerRegistry.RegisterForNavigation<YouYuSettingsPage, YouYuSettingsViewModel>();
            containerRegistry.RegisterSingleton<IYouYuService, YouYuService>();
        }

        public void OnInitialized(IContainerProvider containerProvider)
        {
            // 可选的初始化逻辑
        }
    }
} 