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
        }

        public void OnInitialized(IContainerProvider containerProvider)
        {
            Log.Information("IntegratedCameraModule: 模块初始化开始。");
            // 解析并启动一体化相机服务
            var integratedCameraService = containerProvider.Resolve<ICameraService>(); // 通常解析接口
            if (integratedCameraService is HikvisionIntegratedCameraService hikIntegratedService)
            {
                Log.Information("IntegratedCameraModule: 正在启动海康一体化相机服务 (ICameraService)...");
                hikIntegratedService.Start();
                Log.Information("IntegratedCameraModule: 海康一体化相机服务 (ICameraService) 启动尝试完成。");
            }
            else
            {
                Log.Warning("IntegratedCameraModule: 解析的 ICameraService 不是 HikvisionIntegratedCameraService 类型。服务可能未正确注册或被其他模块覆盖。");
            }

            // 解析并启动安防相机服务
            try
            {
                var securityCameraService = containerProvider.Resolve<HikvisionSecurityCameraService>();
                Log.Information("IntegratedCameraModule: 正在启动海康安防相机服务...");
                securityCameraService.Start();
                Log.Information("IntegratedCameraModule: 海康安防相机服务启动尝试完成。");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "IntegratedCameraModule: 启动海康安防相机服务时发生异常。");
            }

            Log.Information("IntegratedCameraModule: 模块初始化完成。");
        }
    }
} 