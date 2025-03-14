using Common.Services.Settings;
using Microsoft.Extensions.Hosting;
using Modules.Models;
using Serilog;

namespace Modules.Services;

internal class ModuleConnectionHostedService(
    IModuleConnectionService moduleConnectionService,
    ISettingsService settingsService)
    : IHostedService
{
    private readonly ModuleConfig _config = settingsService.LoadSettings<ModuleConfig>();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Log.Information("正在启动模组连接服务...");

            // 启动TCP服务器
            var success = await moduleConnectionService.StartServerAsync(_config.Address, _config.Port);
            if (success)
                Log.Information("模组连接服务已启动，监听地址：{ConfigAddress}:{ConfigPort}", _config.Address, _config.Port);
            else
                Log.Warning("模组连接服务启动失败");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动模组连接服务时发生错误");
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            Log.Information("正在停止模组连接服务...");
            await moduleConnectionService.StopServerAsync();
            Log.Information("模组连接服务已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止模组连接服务时发生错误");
            throw;
        }
    }
}