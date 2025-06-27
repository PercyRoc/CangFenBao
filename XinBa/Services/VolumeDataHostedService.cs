using Common.Services.Settings;
using Microsoft.Extensions.Hosting;
using Serilog;
using XinBa.Services.Models;

namespace XinBa.Services;

/// <summary>
/// Manages the lifecycle of the VolumeDataService.
/// </summary>
public class VolumeDataHostedService : IHostedService
{
    private readonly VolumeDataService _volumeDataService;
    private readonly ISettingsService _settingsService;

    public VolumeDataHostedService(VolumeDataService volumeDataService, ISettingsService settingsService)
    {
        _volumeDataService = volumeDataService ?? throw new ArgumentNullException(nameof(volumeDataService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        Log.Information("VolumeDataHostedService 已创建。");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Log.Information("VolumeDataHostedService 正在启动...");
        try
        {
            var settings = _settingsService.LoadSettings<VolumeCameraSettings>();

            if (!string.IsNullOrEmpty(settings.IpAddress) && settings.Port > 0)
            {
                _volumeDataService.Start();
                Log.Information("VolumeDataHostedService 已尝试启动 VolumeDataService。");
            }
            else
            {
                Log.Warning("VolumeDataHostedService: 体积相机设置无效或未配置，VolumeDataService 未启动。");
            }


        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动 VolumeDataHostedService 时发生错误。");
            // Decide if this error should prevent app startup
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Log.Information("VolumeDataHostedService 正在停止...");
        try
        {
            // Stop/Dispose the VolumeDataService
            _volumeDataService.Stop(); // Stop calls Dispose internally
            Log.Information("VolumeDataHostedService 已停止 VolumeDataService。");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止 VolumeDataHostedService 时发生错误。");
        }
        return Task.CompletedTask;
    }
}