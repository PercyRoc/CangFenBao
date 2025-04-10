using Common.Services.Ui;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace DeviceService.DataSourceDevices.Camera;

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
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Log.Information("正在启动相机服务...");
            var camera = GetCameraService();

            if (!camera.Start())
            {
                const string message = "相机启动失败，请检查日志";
                Log.Warning(message);
                notificationService.ShowError(message);
            }
            else
            {
                Log.Information("相机服务已成功启动");
                notificationService.ShowSuccess("相机服务已成功启动");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动相机服务时发生错误");
            notificationService.ShowError(ex.Message);
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

            if (_cameraService != null)
            {
                try
                {
                    if (!_cameraService.Stop()) Log.Warning("相机停止失败");
                    _cameraService.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "停止相机服务时发生错误");
                }

                _cameraService = null;
            }

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
    internal ICameraService GetCameraService()
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