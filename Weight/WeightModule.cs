using Serilog;
using Weight.Services;
using Weight.ViewModels.Settings;
using Weight.Views.Settings;

namespace Weight;

public class WeightModule : IModule
{
    public void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // 注册服务
        containerRegistry.RegisterSingleton<IWeightService, WeightService>();

        // 注册设置对话框
        containerRegistry.RegisterForNavigation<WeightSettingsView, WeightSettingsViewModel>();
    }

    public void OnInitialized(IContainerProvider containerProvider)
    {
        Log.Information("称重模块 (WeightModule) OnInitialized：尝试启动称重服务。");
        var weightService = containerProvider.Resolve<IWeightService>();
        _ = weightService.ConnectAsync();
    }
}