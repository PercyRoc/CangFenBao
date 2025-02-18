using DeviceService.Camera;
using Microsoft.Extensions.Hosting;
using Presentation_CommonLibrary.Services;
using Prism.Ioc;

namespace DeviceService;

/// <summary>
///     服务注册扩展
/// </summary>
public static class ContainerRegistryExtensions
{
    /// <summary>
    ///     添加设备服务
    /// </summary>
    public static IContainerRegistry AddDeviceServices(this IContainerRegistry containerRegistry)
    {
        // 注册通知服务
        containerRegistry.RegisterSingleton<INotificationService, NotificationService>();
        containerRegistry.RegisterSingleton<IDialogService, DialogService>();

        // 注册相机工厂
        containerRegistry.RegisterSingleton<CameraFactory>();

        // 注册相机启动服务
        containerRegistry.RegisterSingleton<CameraStartupService>();
        containerRegistry.RegisterSingleton<IHostedService>(sp =>
            sp.Resolve<CameraStartupService>());

        // 注册相机服务（从启动服务获取实例）
        containerRegistry.RegisterSingleton<ICameraService>(sp =>
            sp.Resolve<CameraStartupService>().GetCameraService());

        // 注册包裹中转服务
        containerRegistry.RegisterSingleton<PackageTransferService>();

        return containerRegistry;
    }
}