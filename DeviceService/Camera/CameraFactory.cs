using CommonLibrary.Models.Settings.Camera;
using CommonLibrary.Models.Settings.Camera.Enums;
using CommonLibrary.Services;
using DeviceService.Camera.DaHua;
using DeviceService.Camera.Hikvision;
using Serilog;

namespace DeviceService.Camera;

/// <summary>
///     相机工厂
/// </summary>
public class CameraFactory : IAsyncDisposable
{
    private readonly ISettingsService _settingsService;
    private ICameraService? _currentCamera;
    private bool _disposed;

    /// <summary>
    ///     初始化相机工厂
    /// </summary>
    public CameraFactory(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _currentCamera?.DisposeAsync();
            _currentCamera = null;
            _disposed = true;
        }

        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     创建相机服务
    /// </summary>
    public ICameraService CreateCamera()
    {
        var settings = LoadCameraSettings();
        var camera = CreateCameraByManufacturer(settings.Manufacturer);
        camera.UpdateConfiguration(settings);
        return camera;
    }

    /// <summary>
    ///     根据厂商创建相机服务
    /// </summary>
    /// <param name="manufacturer">相机厂商</param>
    /// <returns>相机服务实例</returns>
    public ICameraService CreateCameraByManufacturer(CameraManufacturer manufacturer)
    {
        _currentCamera?.DisposeAsync();
        try
        {
            _currentCamera = manufacturer switch
            {
                CameraManufacturer.Dahua => new DahuaCameraService(),
                CameraManufacturer.Hikvision => new HikvisionIndustrialCameraSdkClient(),
                _ => throw new ArgumentException($"不支持的相机厂商: {manufacturer}")
            };

            Log.Information("已创建 {Manufacturer} 相机服务", manufacturer);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "创建 {Manufacturer} 相机服务失败，将使用大华相机作为默认选项", manufacturer);
            _currentCamera = new DahuaCameraService();
        }

        return _currentCamera;
    }

    /// <summary>
    ///     加载相机设置
    /// </summary>
    private CameraSettings LoadCameraSettings()
    {
        try
        {
            return _settingsService.LoadSettings<CameraSettings>("CameraSettings");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载相机设置失败，使用默认设置");
            return new CameraSettings { Manufacturer = CameraManufacturer.Dahua };
        }
    }
}