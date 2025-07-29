using Common.Services.Settings;
using Common.Services.Ui;
using DeviceService.DataSourceDevices.Camera.HuaRay;
using DeviceService.DataSourceDevices.Camera.Models.Camera;
using DeviceService.DataSourceDevices.Camera.Models.Camera.Enums;
using DeviceService.DataSourceDevices.Camera.TCP;
using Serilog;
// using DeviceService.DataSourceDevices.Camera.Hikvision;
// using DeviceService.DataSourceDevices.Camera.HikvisionSmartSdk;

namespace DeviceService.DataSourceDevices.Camera;

/// <summary>
///     相机工厂
/// </summary>
public class CameraFactory : IDisposable
{
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;
    private ICameraService? _currentCamera;
    private bool _disposed;

    /// <summary>
    ///     初始化相机工厂
    /// </summary>
    public CameraFactory(ISettingsService settingsService, INotificationService notificationService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;
        InitializeCamera();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_currentCamera != null)
            {
                try
                {
                    _currentCamera.Stop();
                    _currentCamera.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "停止相机失败");
                }

                _currentCamera = null;
            }

            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    private void InitializeCamera()
    {
        try
        {
            var settings = LoadCameraSettings();
            _currentCamera = CreateCameraByManufacturer(settings.Manufacturer, settings.CameraType);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化相机失败");
            _notificationService.ShowError($"初始化相机失败: {ex.Message}");
        }
    }

    /// <summary>
    ///     创建相机服务
    /// </summary>
    internal ICameraService CreateCamera()
    {
        var settings = LoadCameraSettings();
        var camera = CreateCameraByManufacturer(settings.Manufacturer, settings.CameraType);
        return camera;
    }

    /// <summary>
    ///     根据厂商创建相机服务
    /// </summary>
    /// <param name="manufacturer">相机厂商</param>
    /// <param name="cameraType">相机类型</param>
    /// <returns>相机服务实例</returns>
    private static ICameraService CreateCameraByManufacturer(CameraManufacturer manufacturer, CameraType cameraType)
    {
        try
        {
            ICameraService camera = manufacturer switch
            {
                CameraManufacturer.HuaRay => new HuaRayCameraService(),
                // CameraManufacturer.Hikvision when cameraType == CameraType.Industrial =>
                //     new HikvisionIndustrialCameraService(),
                // CameraManufacturer.Hikvision when cameraType == CameraType.Smart =>
                //     new HikvisionSmartCameraService(),
                CameraManufacturer.Tcp => new TcpCameraService(),
                _ => throw new ArgumentException($"不支持的相机厂商和类型组合: {manufacturer} - {cameraType}")
            };
            Log.Information("创建 {Manufacturer} {Type} 相机服务成功", manufacturer, cameraType);
            return camera;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "创建 {Manufacturer} {Type} 相机服务失败，将使用华睿相机作为默认选项", manufacturer, cameraType);
            return new HuaRayCameraService();
        }
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
            return new CameraSettings
            {
                Manufacturer = CameraManufacturer.HuaRay
            };
        }
    }
}