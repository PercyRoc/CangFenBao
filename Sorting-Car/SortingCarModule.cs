using SharedUI.Views.Settings;
using Sorting_Car.ViewModels;
using Sorting_Car.Views;
using Sorting_Car.Services;
using Serilog;

namespace Sorting_Car
{
    public class SortingCarModule : IModule
    {
        public void RegisterTypes(IContainerRegistry containerRegistry)
        {
            // 注册设置对话框
            containerRegistry.RegisterForNavigation<CarSerialPortSettingsView, CarSerialPortSettingsViewModel>();
            containerRegistry.RegisterForNavigation<CarConfigView, CarConfigViewModel>();
            containerRegistry.RegisterForNavigation<CarSequenceView, CarSequenceViewModel>();

            // 注册服务为单例
            containerRegistry.RegisterSingleton<CarSortingService>();
            containerRegistry.RegisterSingleton<CarSortService>();
        }

        public void OnInitialized(IContainerProvider containerProvider)
        {
            // 模块初始化后启动分拣服务
            var sortService = containerProvider.Resolve<CarSortService>();
            if (sortService.InitializeAsync().Result)
            {
                sortService.StartAsync();
                Log.Information("小车分拣服务已自动启动。");
            }
            else
            {
                Log.Error("小车分拣服务初始化失败，未能自动启动。");
            }
        }
    }
}
