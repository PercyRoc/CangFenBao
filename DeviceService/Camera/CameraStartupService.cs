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
    IDialogService dialogService) : IHostedService
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
            Log.Information("正在启动相机服务...");
            var camera = GetCameraService();

            if (!camera.Start())
            {
                const string message = "相机服务启动失败";
                Log.Warning(message);
                await dialogService.ShowErrorAsync(message, "相机服务错误");
            }
            else
            {
                Log.Information("相机服务启动成功");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动相机服务时发生错误");
            await dialogService.ShowErrorAsync(ex.Message, "相机服务错误");
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