using SowingSorting.Models.Settings;
using SowingSorting.Services;
using Common.Services.Settings;
using SowingSorting.Views.Settings;
using SowingSorting.ViewModels.Settings;

namespace SowingSorting
{
    public class SowingSortingModule : IModule
    {
        public async void OnInitialized(IContainerProvider containerProvider)
        {
            // 自动启动 Modbus 连接
            var settingsService = containerProvider.Resolve<ISettingsService>();
            var modbusService = containerProvider.Resolve<IModbusTcpService>();
            var settings = settingsService.LoadSettings<ModbusTcpSettings>("SowingSorting.ModbusTcp");

            if (!string.IsNullOrWhiteSpace(settings.IpAddress) && settings.Port is > 0 and <= 65535)
            {
                await modbusService.ConnectAsync(settings);
            }
        }

        public void RegisterTypes(IContainerRegistry containerRegistry)
        {
            // 注册服务
            containerRegistry.RegisterSingleton<IModbusTcpService, ModbusTcpService>();
            // 注册用于导航的视图
            // 第一个参数是导航时使用的唯一键名
            containerRegistry.RegisterSingleton<ModbusTcpSettingsViewModel>();
            containerRegistry.RegisterForNavigation<ModbusTcpSettingsView, ModbusTcpSettingsViewModel>();
        }
    }
} 