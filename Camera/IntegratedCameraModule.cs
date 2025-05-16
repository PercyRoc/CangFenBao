using Camera.Interface;
using Camera.Services.Implementations.Hikvision.Integrated;
using Camera.Services.Implementations.Hikvision.Security;
using Serilog;

namespace Camera
{
    public class IntegratedCameraModule : IModule
    {
        public void RegisterTypes(IContainerRegistry containerRegistry)
        {
            // 注册海康一体化相机服务
            containerRegistry.RegisterSingleton<ICameraService, HikvisionIntegratedCameraService>();
            containerRegistry.RegisterSingleton<HikvisionSecurityCameraService>();
            Log.Information("IntegratedCameraModule: HikvisionIntegratedCameraService 已注册为 ICameraService。");
        }

        public void OnInitialized(IContainerProvider containerProvider)
        {
            Log.Information("IntegratedCameraModule: 模块初始化开始。");
            // 解析并启动相机服务
            var cameraService = containerProvider.Resolve<ICameraService>();
            if (cameraService is HikvisionIntegratedCameraService integratedService)
            {
                Log.Information("IntegratedCameraModule: 正在启动 HikvisionIntegratedCameraService...");
                integratedService.Start();
                Log.Information("IntegratedCameraModule: HikvisionIntegratedCameraService 启动已调用。");
            }
            else
            {
                Log.Warning("IntegratedCameraModule: 解析的 ICameraService 不是 HikvisionIntegratedCameraService 类型。服务可能未正确注册或被其他模块覆盖。");
            }
            Log.Information("IntegratedCameraModule: 模块初始化完成。");
        }
    }
} 