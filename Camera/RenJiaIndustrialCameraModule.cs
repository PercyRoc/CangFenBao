using Camera.Interface;
using Camera.Services.Implementations.Hikvision.Industrial;
using Camera.Services.Implementations.RenJia; // 添加人加相机服务的 using
using Serilog;

namespace Camera;

/// <summary>
/// 相机模块，负责注册和初始化各类相机服务。
/// </summary>
public class RenJiaIndustrialCameraModule : IModule // 类名已更改
{
    /// <summary>
    /// 注册模块的类型和服务。
    /// </summary>
    /// <param name="containerRegistry">依赖注入容器注册表。</param>
    public void RegisterTypes(IContainerRegistry containerRegistry)
    {
        containerRegistry.RegisterSingleton<ICameraService, HikvisionIndustrialCameraService>();
        Log.Information("相机模块: HikvisionIndustrialCameraService 已注册为默认 ICameraService 的单例。");

        // 注册人加体积相机服务（具体类型）
        containerRegistry.RegisterSingleton<RenJiaCameraService>();
        Log.Information("相机模块: RenJiaCameraService 已注册为单例。");
    }

    /// <summary>
    /// 当模块初始化完成时调用。
    /// </summary>
    /// <param name="containerProvider">依赖注入容器提供者。</param>
    public void OnInitialized(IContainerProvider containerProvider)
    {
        Log.Information("相机模块已初始化。");

        // 启动默认的 ICameraService (海康工业相机)
        TryStartDefaultCameraService(containerProvider);

        // 启动人加体积相机服务
        TryStartRenJiaCameraService(containerProvider);
    }

    private static void TryStartDefaultCameraService(IContainerProvider containerProvider)
    {
        try
        {
            var cameraService = containerProvider.Resolve<ICameraService>();
            Log.Information("相机模块: 正在尝试自动启动默认相机服务 (ICameraService)...");
            _ = Task.Run(() =>
            {
                try
                {
                    if (cameraService.Start())
                    {
                        Log.Information("相机模块: 默认相机服务 (ICameraService) 已成功启动。");
                    }
                    else
                    {
                        Log.Warning("相机模块: 默认相机服务 (ICameraService) 启动失败（Start方法返回false）。");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "相机模块: 启动默认相机服务 (ICameraService) 时发生异常。");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "相机模块: 解析或尝试启动默认相机服务 (ICameraService) 时发生外部异常。");
        }
    }

    private static void TryStartRenJiaCameraService(IContainerProvider containerProvider)
    {
        try
        {
            var renJiaCameraService = containerProvider.Resolve<RenJiaCameraService>();
            Log.Information("相机模块: 正在尝试自动启动人加体积相机服务 (RenJiaCameraService)...");
            _ = Task.Run(() =>
            {
                try
                {
                    if (renJiaCameraService.Start()) // RenJiaCameraService 也实现了 Start()
                    {
                        Log.Information("相机模块: 人加体积相机服务 (RenJiaCameraService) 已成功启动。");
                    }
                    else
                    {
                        Log.Warning("相机模块: 人加体积相机服务 (RenJiaCameraService) 启动失败（Start方法返回false）。");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "相机模块: 启动人加体积相机服务 (RenJiaCameraService) 时发生异常。");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "相机模块: 解析或尝试启动人加体积相机服务 (RenJiaCameraService) 时发生外部异常。");
        }
    }
} 