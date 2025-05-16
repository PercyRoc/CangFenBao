using Camera.Interface;
using Camera.Services;
using Camera.Services.Implementations.Hikvision.Industrial;
using Camera.Services.Implementations.Hikvision.Security;
using Camera.Services.Implementations.Hikvision.Volume;
using Camera.ViewModels;
using Camera.Views;
using Serilog;

namespace Camera
{
    public class FullFeaturedCameraModule : IModule
    {
        public void OnInitialized(IContainerProvider containerProvider)
        {
            Log.Information("CameraModule: 正在初始化并启动相机服务...");

            var cameraService = containerProvider.Resolve<ICameraService>();
            var securityCameraService = containerProvider.Resolve<HikvisionSecurityCameraService>();
            var volumeCameraService = containerProvider.Resolve<HikvisionVolumeCameraService>();
            var dataProcessingService = containerProvider.Resolve<CameraDataProcessingService>();

            _ = Task.Run(() =>
            {
                cameraService.Start();
                Log.Information("ICameraService (HikvisionIndustrialCameraService) 已启动。");

                securityCameraService.Start();
                Log.Information("HikvisionSecurityCameraService 已启动。");

                volumeCameraService.Start();
                Log.Information("HikvisionVolumeCameraService 已启动。");

                dataProcessingService.Start();
                Log.Information("CameraDataProcessingService 已启动。");
            });
            Log.Information("CameraModule: 相机服务启动已发起。");
        }

        public void RegisterTypes(IContainerRegistry containerRegistry)
        {
            containerRegistry.RegisterForNavigation<CameraSettingsView, CameraSettingsViewModel>();

            // 注册 ICameraService 的主要实现
            containerRegistry.RegisterSingleton<ICameraService, HikvisionIndustrialCameraService>();
            containerRegistry.RegisterSingleton<HikvisionSecurityCameraService>();
            containerRegistry.RegisterSingleton<HikvisionVolumeCameraService>();
            containerRegistry.RegisterSingleton<CameraDataProcessingService>();
        }
    }
}