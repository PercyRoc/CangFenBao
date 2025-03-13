using Common.Services.Settings;
using Common.Services.Ui;
using DeviceService.DataSourceDevices.Camera.Models.Camera;
using DeviceService.DataSourceDevices.Camera.RenJia;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace DeviceService.DataSourceDevices.Camera;

/// <summary>
///     体积相机启动服务
/// </summary>
public class VolumeCameraStartupService : IHostedService
{
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;
    private RenJiaCameraService? _cameraService;

    /// <summary>
    ///     构造函数
    /// </summary>
    public VolumeCameraStartupService(
        INotificationService notificationService,
        ISettingsService settingsService)
    {
        _notificationService = notificationService;
        _settingsService = settingsService;
        _settingsService.OnSettingsChanged<VolumeSettings>(OnVolumeSettingsChanged);
    }

    /// <summary>
    ///     启动服务
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var camera = GetCameraService();
            var config = _settingsService.LoadSettings<VolumeSettings>();
            camera.UpdateConfiguration(config);

            if (!camera.Start())
            {
                const string message = "Failed to start volume camera service";
                Log.Warning(message);
                _notificationService.ShowError(message);
            }
            else
            {
                Log.Information("体积相机已成功启动");
                _notificationService.ShowSuccess("Volume camera service started successfully");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动体积相机服务时发生错误");
            _notificationService.ShowError(ex.Message);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///     停止服务
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            Log.Information("正在停止体积相机服务...");

            if (_cameraService != null)
                try
                {
                    if (!_cameraService.Stop()) Log.Warning("体积相机停止未成功完成");
                    // 释放资源
                     _cameraService.Dispose();
                    _cameraService = null;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "停止体积相机服务时发生错误");
                    _cameraService = null;
                }

            Log.Information("体积相机服务已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止体积相机服务时发生错误");
        }
        finally
        {
            _initLock.Dispose();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///     获取相机服务实例
    /// </summary>
    public RenJiaCameraService GetCameraService()
    {
        _initLock.Wait();
        try
        {
            return _cameraService ??= new RenJiaCameraService();
        }
        finally
        {
            _initLock.Release();
        }
    }

    private void OnVolumeSettingsChanged(VolumeSettings settings)
    {
        try
        {
            Log.Information("检测到体积相机配置变更，准备更新配置...");
            var camera = GetCameraService();
            camera.UpdateConfiguration(settings);
            Log.Information("体积相机配置更新成功");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新体积相机配置时发生错误");
            _notificationService.ShowError("更新体积相机配置失败：" + ex.Message);
        }
    }
}