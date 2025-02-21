using Microsoft.Extensions.Hosting;
using Presentation_CommonLibrary.Services;
using Serilog;

namespace DeviceService.Camera;

/// <summary>
///     相机启动服务
/// </summary>
/// <remarks>
///     构造函数
/// </remarks>
public class CameraStartupService(
    CameraFactory cameraFactory,
    INotificationService notificationService) : IHostedService
{
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private ICameraService? _cameraService;

    /// <summary>
    ///     启动服务
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Log.Information("Starting camera service...");
            var camera = GetCameraService();

            if (!camera.Start())
            {
                const string message = "Failed to start camera service";
                Log.Warning(message);
                notificationService.ShowError(message, "Camera Service Error");
            }
            else
            {
                Log.Information("Camera service started successfully");
                notificationService.ShowSuccess("Camera service started successfully");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动相机服务时发生错误");
            notificationService.ShowError(ex.Message, "相机服务错误");
        }
    }

    /// <summary>
    ///     停止服务
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            Log.Information("正在停止相机服务...");
            _cameraService?.Stop();
            _cameraService?.Dispose();
            _cameraService = null;
            Log.Information("相机服务已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止相机服务时发生错误");
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
    public ICameraService GetCameraService()
    {
        _initLock.Wait();
        try
        {
            return _cameraService ??= cameraFactory.CreateCamera();
        }
        finally
        {
            _initLock.Release();
        }
    }
}