using Server.JuShuiTan.Services;
using Server.JuShuiTan.ViewModels;
using Server.JuShuiTan.Views;

namespace Server.JuShuiTan;

public class JuShuiTanModule: IModule
{
    public void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // 注册聚水潭服务配置页面
        containerRegistry.RegisterForNavigation<JushuitanSettingsPage, JushuitanSettingsViewModel>();
        containerRegistry.RegisterSingleton<IJuShuiTanService, JuShuiTanService>();
    }

    public void OnInitialized(IContainerProvider containerProvider)
    {
        // 可选：初始化逻辑
    }
}