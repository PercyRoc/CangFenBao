using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Camera.RenJia;
using DeviceService.DataSourceDevices.Scanner;
using DeviceService.DataSourceDevices.Services;
using DeviceService.DataSourceDevices.Weight;
using Prism.Ioc;

namespace DeviceService.Extensions;

/// <summary>
///     服务注册扩展
/// </summary>
public static class ContainerRegistryExtensions
{
    /// <summary>
    ///     添加拍照相机服务
    /// </summary>
    public static IContainerRegistry AddPhotoCamera(this IContainerRegistry containerRegistry)
    {
        // 注册相机工厂
        containerRegistry.RegisterSingleton<CameraFactory>();
        containerRegistry.RegisterSingleton<PackageTransferService>();
        containerRegistry.RegisterSingleton<IImageSavingService, ImageSavingService>();
        // 注册相机启动服务
        containerRegistry.RegisterSingleton<CameraStartupService>();

        // 注册相机服务（从启动服务获取实例）
        containerRegistry.RegisterSingleton<ICameraService>(static sp =>
            sp.Resolve<CameraStartupService>().GetCameraService());

        return containerRegistry;
    }

    /// <summary>
    ///     添加体积相机服务
    /// </summary>
    public static IContainerRegistry AddVolumeCamera(this IContainerRegistry containerRegistry)
    {
        // 注册体积相机启动服务
        containerRegistry.RegisterSingleton<VolumeCameraStartupService>();

        containerRegistry.RegisterSingleton<RenJiaCameraService>(static sp =>
            sp.Resolve<VolumeCameraStartupService>().GetCameraService());

        return containerRegistry;
    }

    /// <summary>
    ///     添加扫码枪服务
    /// </summary>
    public static IContainerRegistry AddScanner(this IContainerRegistry containerRegistry)
    {
        // 注册扫码枪启动服务
        containerRegistry.RegisterSingleton<ScannerStartupService>();

        containerRegistry.RegisterSingleton<IScannerService>(static sp =>
            sp.Resolve<ScannerStartupService>().GetScannerService());

        return containerRegistry;
    }

    /// <summary>
    ///     添加重量称服务
    /// </summary>
    public static void AddWeightScale(this IContainerRegistry containerRegistry)
    {
        // 注册重量称启动服务
        containerRegistry.RegisterSingleton<WeightStartupService>();

        containerRegistry.RegisterSingleton<SerialPortWeightService>(static sp =>
            sp.Resolve<WeightStartupService>().GetWeightService());
    }
}