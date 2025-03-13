using Common.Services;
using Common.Services.Settings;
using Microsoft.Extensions.Hosting;
using Presentation_SeedingWall.Models;
using Serilog;

namespace Presentation_SeedingWall.Services;

/// <summary>
///     PLC启动服务
/// </summary>
public class PlcStartupService : IHostedService
{
    private readonly IPlcService _plcService;
    private readonly ISettingsService _settingsService;
    private PlcSettings _settings;

    /// <summary>
    ///     构造函数
    /// </summary>
    /// <param name="plcService">PLC服务</param>
    /// <param name="settingsService">设置服务</param>
    public PlcStartupService(IPlcService plcService, ISettingsService settingsService)
    {
        _plcService = plcService;
        _settingsService = settingsService;
        _settings = new PlcSettings();
    }

    /// <summary>
    ///     启动服务
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>任务</returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 加载配置
            _settings = _settingsService.LoadSettings<PlcSettings>();


            Log.Information("正在自动启动PLC服务...");
            var result = await _plcService.StartServerAsync(_settings.ServerIp, _settings.ServerPort);

            if (result)
                Log.Information("PLC服务已自动启动");
            else
                Log.Warning("PLC服务自动启动失败");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动PLC服务时发生错误");
        }
    }

    /// <summary>
    ///     停止服务
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>任务</returns>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            Log.Information("正在停止PLC服务...");
            _plcService.StopServer();
            Log.Information("PLC服务已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止PLC服务时发生错误");
        }

        return Task.CompletedTask;
    }
}