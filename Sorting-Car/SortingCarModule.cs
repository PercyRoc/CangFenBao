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
            containerRegistry.RegisterSingleton<CarSerialPortSettingsViewModel>();
            containerRegistry.RegisterSingleton<CarConfigViewModel>();
            containerRegistry.RegisterSingleton<CarSequenceViewModel>();
            containerRegistry.RegisterForNavigation<CarSerialPortSettingsView, CarSerialPortSettingsViewModel>();
            containerRegistry.RegisterForNavigation<CarConfigView, CarConfigViewModel>();
            containerRegistry.RegisterForNavigation<CarSequenceView, CarSequenceViewModel>();

            // 注册服务为单例
            containerRegistry.RegisterSingleton<ICarSortingDevice, CarSortingService>();
            containerRegistry.RegisterSingleton<CarSortService>();
        }

        public async void OnInitialized(IContainerProvider containerProvider)
        {
            try
            {
                var sortService = containerProvider.Resolve<CarSortService>();
                if (await sortService.StartAsync())
                {
                    Log.Information("小车分拣服务已自动启动。");
                }
                else
                {
                    Log.Error("小车分拣服务未能自动启动。");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "在 SortingCarModule 初始化时启动 CarSortService 失败。");
            }
        }
    }
}
