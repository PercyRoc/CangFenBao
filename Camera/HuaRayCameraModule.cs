using Camera.Interface;
using Camera.Services.Implementations.HuaRay;
using Prism.Ioc;
using Prism.Modularity;
using Serilog;

namespace Camera
{
    /// <summary>
    /// 专门用于注册和启动 HuaRayCameraService 的模块。
    /// </summary>
    public class HuaRayCameraModule : IModule
    {
        /// <summary>
        /// 注册 HuaRay 相机服务为 ICameraService 单例。
        /// </summary>
        /// <param name="containerRegistry">依赖注入容器注册表。</param>
        public void RegisterTypes(IContainerRegistry containerRegistry)
        {
            containerRegistry.RegisterSingleton<ICameraService, HuaRayCameraService>();
        }

        /// <summary>
        /// 模块初始化时启动 HuaRay 相机服务。
        /// </summary>
        /// <param name="containerProvider">依赖注入容器提供程序。</param>
        public void OnInitialized(IContainerProvider containerProvider)
        {
            Log.Information("[HuaRayCameraModule] 模块初始化开始...");
            try
            {
                // 解析 HuaRayCameraService 实例
                var huaRayCameraService = containerProvider.Resolve<ICameraService>();
                Log.Information("[HuaRayCameraModule] 正在启动 HuaRayCameraService...");
                var success = huaRayCameraService.Start();
                if (success)
                {
                    Log.Information("[HuaRayCameraModule] HuaRayCameraService 启动成功。");
                }
                else
                {
                    Log.Warning("[HuaRayCameraModule] HuaRayCameraService 启动失败，请检查日志。");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[HuaRayCameraModule] 初始化或启动 HuaRayCameraService 时发生异常。");
            }
            Log.Information("[HuaRayCameraModule] 模块初始化完成。");
        }
    }
} 