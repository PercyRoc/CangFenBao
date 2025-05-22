using Camera.Services.Implementations.TCP;
using Serilog;

namespace Camera
{
    /// <summary>
    /// 专门用于注册和启动 TcpCameraService 的模块。
    /// </summary>
    public class TcpCameraModule : IModule
    {
        /// <summary>
        /// 注册 Tcp 相机服务为 ICameraService 单例。
        /// </summary>
        /// <param name="containerRegistry">依赖注入容器注册表。</param>
        public void RegisterTypes(IContainerRegistry containerRegistry)
        {
            containerRegistry.RegisterSingleton<TcpCameraService>();
        }

        /// <summary>
        /// 模块初始化时启动 Tcp 相机服务。
        /// </summary>
        /// <param name="containerProvider">依赖注入容器提供程序。</param>
        public void OnInitialized(IContainerProvider containerProvider)
        {
            Log.Information("[TcpCameraModule] 模块初始化开始...");
            try
            {
                // 解析 TcpCameraService 实例
                var tcpCameraService = containerProvider.Resolve<TcpCameraService>();
                Log.Information("[TcpCameraModule] 正在启动 TcpCameraService...");
                bool success = tcpCameraService.Start();
                if (success)
                {
                    Log.Information("[TcpCameraModule] TcpCameraService 启动成功。");
                }
                else
                {
                    Log.Warning("[TcpCameraModule] TcpCameraService 启动失败，请检查日志。");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[TcpCameraModule] 初始化或启动 TcpCameraService 时发生异常。");
            }
            Log.Information("[TcpCameraModule] 模块初始化完成。");
        }
    }
} 