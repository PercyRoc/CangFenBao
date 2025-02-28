using CommonLibrary.Models.Settings.Camera;
using CommonLibrary.Services;
using DeviceService.Camera.RenJia;
using Microsoft.Extensions.Hosting;
using Presentation_CommonLibrary.Services;
using Serilog;

namespace DeviceService.Camera;

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
    }

    /// <summary>
    ///     启动服务
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Log.Information("Starting volume camera service...");
            var camera = GetCameraService();

            // Load configuration
            Log.Debug("Loading volume camera configuration...");
            var config = _settingsService.LoadConfiguration<VolumeSettings>();

            // Update configuration
            Log.Debug("Updating volume camera configuration...");
            camera.UpdateConfiguration(config);

            if (!camera.Start())
            {
                const string message = "Failed to start volume camera service";
                Log.Warning(message);
                _notificationService.ShowError(message, "Volume Camera Service Error");
            }
            else
            {
                Log.Information("Volume camera service started successfully");
                _notificationService.ShowSuccess("Volume camera service started successfully");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动体积相机服务时发生错误");
            _notificationService.ShowError(ex.Message, "体积相机服务错误");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///     停止服务
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            Log.Information("正在停止体积相机服务...");

            if (_cameraService != null)
                try
                {
                    // 使用异步停止方法并设置超时保护
                    var stopResult = await _cameraService.StopAsync(5000);
                    if (!stopResult) Log.Warning("体积相机异步停止未成功完成");

                    // 使用异步释放资源
                    await _cameraService.DisposeAsync();
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
}