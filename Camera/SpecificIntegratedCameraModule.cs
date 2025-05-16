using Camera.Interface;
using Camera.Services.Implementations.Hikvision.Integrated;
using Prism.Ioc;
using Prism.Modularity;
using Serilog;

namespace Camera
{
    /// <summary>
    /// 一个专门用于注册和启动 HikvisionIntegratedCameraService 的模块。
    /// </summary>
    public class SpecificIntegratedCameraModule : IModule
    {
        /// <summary>
        /// 注册模块相关的类型。
        /// </summary>
        /// <param name="containerRegistry">依赖注入容器注册表。</param>
        public void RegisterTypes(IContainerRegistry containerRegistry)
        {
            Log.Information("[SpecificIntegratedCameraModule] 正在注册 海康物流SDK...");
            // 将 HikvisionIntegratedCameraService 注册为 ICameraService 的单例实现
            containerRegistry.RegisterSingleton<ICameraService, HikvisionIntegratedCameraService>();
            // 同时注册其具体类型，如果需要直接解析具体类型实例
            containerRegistry.RegisterSingleton<HikvisionIntegratedCameraService>();
            Log.Information("[SpecificIntegratedCameraModule] 海康物流SDK 已注册。");
        }

        /// <summary>
        /// 在模块初始化时调用。
        /// </summary>
        /// <param name="containerProvider">依赖注入容器提供程序。</param>
        public void OnInitialized(IContainerProvider containerProvider)
        {
            Log.Information("[SpecificIntegratedCameraModule] 模块初始化开始...");
            try
            {
                // 解析 HikvisionIntegratedCameraService 的具体实例
                // 这里直接解析具体类型，因为我们明确知道这个模块只处理这个特定的服务。
                // 如果在 RegisterTypes 中没有注册具体类型，这里会失败。
                var integratedCameraService = containerProvider.Resolve<HikvisionIntegratedCameraService>();

                {
                    Log.Information("[SpecificIntegratedCameraModule] 正在启动 海康物流SDK...");
                    bool success = integratedCameraService.Start();
                    if (success)
                    {
                        Log.Information("[SpecificIntegratedCameraModule] 海康物流SDK 启动成功。");
                    }
                    else
                    {
                        Log.Warning("[SpecificIntegratedCameraModule] 海康物流SDK 启动失败。请检查日志以获取更多信息。");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[SpecificIntegratedCameraModule] 初始化或启动 海康物流SDK 时发生异常。");
            }
            Log.Information("[SpecificIntegratedCameraModule] 模块初始化完成。");
        }
    }
} 