using JinHuaQiHang.Services;
using JinHuaQiHang.Services.Implementations;
using JinHuaQiHang.ViewModels.Settings;
using JinHuaQiHang.Views.Settings;

namespace JinHuaQiHang
{
    public class JinHuaQiHangModule : IModule
    {
        public void OnInitialized(IContainerProvider containerProvider)
        {
            var regionManager = containerProvider.Resolve<IRegionManager>();
            regionManager.RegisterViewWithRegion("SettingsRegion", typeof(YunDaUploadSettingsView));
        }

        public void RegisterTypes(IContainerRegistry containerRegistry)
        {
            containerRegistry.RegisterSingleton<IYunDaUploadService, YunDaUploadService>();
            containerRegistry.RegisterSingleton<YunDaUploadSettingsViewModel>();
            containerRegistry.RegisterForNavigation<YunDaUploadSettingsView>();
        }
    }
} 