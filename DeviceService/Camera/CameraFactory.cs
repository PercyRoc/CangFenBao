using CommonLibrary.Models.Settings.Camera;
using CommonLibrary.Models.Settings.Camera.Enums;
using CommonLibrary.Services;
using DeviceService.Camera.DaHua;
using Serilog;

namespace DeviceService.Camera;

/// <summary>
///     相机工厂
/// </summary>
/// <remarks>
///     构造函数
/// </remarks>
public class CameraFactory(ISettingsService settingsService) : IDisposable
{
    private ICameraService? _currentCamera;
    private bool _disposed;

    public void Dispose()
    {
        if (!_disposed)
        {
            _currentCamera?.Dispose();
            _currentCamera = null;
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     创建相机服务
    /// </summary>
    public ICameraService CreateCamera()
    {
        var settings = LoadCameraSettings();
        return CreateCameraByManufacturer(settings.Manufacturer);
    }

    /// <summary>
    ///     根据厂商创建相机服务
    /// </summary>
    /// <param name="manufacturer">相机厂商</param>
    /// <returns>相机服务实例</returns>
    public ICameraService CreateCameraByManufacturer(CameraManufacturer manufacturer)
    {
        _currentCamera?.Dispose();
        try
        {
            _currentCamera = manufacturer switch
            {
                CameraManufacturer.Dahua => new DahuaCameraService(),
                CameraManufacturer.Hikvision => throw new NotImplementedException("海康相机暂未实现"),
                _ => throw new ArgumentException($"不支持的相机厂商: {manufacturer}")
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "创建相机服务失败，将使用大华相机作为默认选项");
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
            return settingsService.LoadSettings<CameraSettings>("CameraSettings");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载相机设置失败，使用默认设置");
            return new CameraSettings { Manufacturer = CameraManufacturer.Dahua };
        }
    }
}