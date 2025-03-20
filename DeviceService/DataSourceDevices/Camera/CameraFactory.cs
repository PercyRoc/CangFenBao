using Common.Services.Settings;
using Common.Services.Ui;
using DeviceService.DataSourceDevices.Camera.DaHua;
using DeviceService.DataSourceDevices.Camera.Hikvision;
using DeviceService.DataSourceDevices.Camera.Models.Camera;
using DeviceService.DataSourceDevices.Camera.Models.Camera.Enums;
using DeviceService.DataSourceDevices.Camera.TCP;
using Serilog;

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
        // 注册配置变更事件
        settingsService.OnSettingsChanged<CameraSettings>(OnCameraSettingsChanged);
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

    private void OnCameraSettingsChanged(CameraSettings settings)
    {
        try
        {
            bool needReinitialize = false;
            
            // 检查制造商和类型是否发生变化
            if (_currentCamera == null)
            {
                needReinitialize = true;
            }
            else if (_currentCamera is DahuaCameraService && settings.Manufacturer != CameraManufacturer.Dahua)
            {
                needReinitialize = true;
            }
            else if (_currentCamera is HikvisionIndustrialCameraSdkClient && 
                    (settings.Manufacturer != CameraManufacturer.Hikvision || settings.CameraType != CameraType.Industrial))
            {
                needReinitialize = true;
            }
            else if (_currentCamera is HikvisionSmartCameraService &&
                    (settings.Manufacturer != CameraManufacturer.Hikvision || settings.CameraType != CameraType.Smart))
            {
                needReinitialize = true;
            }
            else if (_currentCamera is TcpCameraService && settings.Manufacturer != CameraManufacturer.Tcp)
            {
                needReinitialize = true;
            }
            
            if (needReinitialize)
            {
                Log.Information("相机制造商或类型发生变更，准备重新初始化相机");
                _notificationService.ShowSuccess("相机制造商或类型发生变更，准备重新初始化相机");

                // 停止并释放旧相机
                if (_currentCamera != null)
                    try
                    {
                        _currentCamera.Stop();
                        _currentCamera.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "停止旧相机失败");
                    }

                try
                {
                    // 创建并初始化新相机
                    _currentCamera = CreateCameraByManufacturer(settings.Manufacturer, settings.CameraType);
                    _currentCamera.UpdateConfiguration(settings);

                    Log.Information("相机重新初始化完成");
                    _notificationService.ShowSuccess("相机重新初始化完成");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "初始化新相机失败");
                    _notificationService.ShowError($"初始化新相机失败: {ex.Message}");
                    throw;
                }
            }
            else
            {
                // 只更新配置
                Log.Information("相机配置发生变更，更新配置");
                _currentCamera?.Stop();
                _currentCamera?.UpdateConfiguration(settings);
                Log.Information("相机配置更新完成");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理相机配置变更失败");
            _notificationService.ShowError($"处理相机配置变更失败: {ex.Message}");
        }
    }

    private void InitializeCamera()
    {
        try
        {
            var settings = LoadCameraSettings();
            _currentCamera = CreateCameraByManufacturer(settings.Manufacturer, settings.CameraType);
            _currentCamera.UpdateConfiguration(settings);
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
        camera.UpdateConfiguration(settings);
        return camera;
    }

    /// <summary>
    ///     根据厂商创建相机服务
    /// </summary>
    /// <param name="manufacturer">相机厂商</param>
    /// <param name="cameraType">相机类型</param>
    /// <returns>相机服务实例</returns>
    public static ICameraService CreateCameraByManufacturer(CameraManufacturer manufacturer, CameraType cameraType)
    {
        try
        {
            ICameraService camera = manufacturer switch
            {
                CameraManufacturer.Dahua => new DahuaCameraService(),
                CameraManufacturer.Hikvision when cameraType == CameraType.Industrial =>
                    new HikvisionIndustrialCameraSdkClient(),
                CameraManufacturer.Hikvision when cameraType == CameraType.Smart => new HikvisionSmartCameraService(),
                CameraManufacturer.Tcp => new TcpCameraService(),
                _ => throw new ArgumentException($"不支持的相机厂商和类型组合: {manufacturer} - {cameraType}")
            };
            Log.Information("已创建 {Manufacturer} {Type} 相机服务", manufacturer, cameraType);
            return camera;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "创建 {Manufacturer} {Type} 相机服务失败，将使用大华相机作为默认选项", manufacturer, cameraType);
            return new DahuaCameraService();
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
            return new CameraSettings { Manufacturer = CameraManufacturer.Dahua };
        }
    }
}