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
    private readonly IDialogService _dialogService;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private RenJiaCameraService? _cameraService;

    /// <summary>
    ///     构造函数
    /// </summary>
    public VolumeCameraStartupService(IDialogService dialogService)
    {
        _dialogService = dialogService;
    }

    /// <summary>
    ///     启动服务
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Log.Information("正在启动体积相机服务...");
            var camera = GetCameraService();

            if (!camera.Start())
            {
                const string message = "体积相机服务启动失败";
                Log.Warning(message);
                await _dialogService.ShowErrorAsync(message, "体积相机服务错误");
            }
            else
            {
                Log.Information("体积相机服务启动成功");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动体积相机服务时发生错误");
            await _dialogService.ShowErrorAsync(ex.Message, "体积相机服务错误");
        }
    }

    /// <summary>
    ///     停止服务
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            Log.Information("正在停止体积相机服务...");
            _cameraService?.Stop();
            _cameraService?.Dispose();
            _cameraService = null;
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
} 