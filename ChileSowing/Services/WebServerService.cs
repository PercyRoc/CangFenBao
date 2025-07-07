using ChileSowing.Models.Settings;
using ChileSowing.Controllers;
using Common.Services.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Microsoft.AspNetCore.Builder;

namespace ChileSowing.Services;

/// <summary>
/// Web服务器服务实现
/// </summary>
public class WebServerService(ISettingsService settingsService, ILogger<WebServerService> logger) : IWebServerService
{
    private IWebHost? _host;
    private bool _isRunning;

    /// <summary>
    /// 是否正在运行
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// 服务器URL
    /// </summary>
    public string? ServerUrl { get; private set; }

    /// <summary>
    /// 启动Web服务器
    /// </summary>
    public async Task StartAsync()
    {
        if (_isRunning)
        {
            logger.LogWarning("Web服务器已在运行，无需重复启动");
            return;
        }

        try
        {
            var settings = settingsService.LoadSettings<WebServerSettings>();
            if (!settings.IsEnabled)
            {
                logger.LogInformation("Web服务器已禁用，不启动");
                return;
            }

            var urls = $"http://*:{settings.Port}";
            
            _host = new WebHostBuilder()
                .UseKestrel()
                .UseUrls(urls)
                .ConfigureServices(services =>
                {
                    services.AddMvc(options => options.EnableEndpointRouting = false);
                    services.AddSingleton(settingsService);
                    services.AddSingleton<BatchOrderController>();
                    services.AddLogging(builder =>
                    {
                        builder.ClearProviders();
                        builder.AddSerilog(Log.Logger);
                    });
                })
                .Configure(app =>
                {
                    app.UseMvc();
                })
                .Build();

            await _host.StartAsync();
            
            _isRunning = true;
            ServerUrl = settings.ServerUrl;
            
            logger.LogInformation("Web服务器已启动，监听地址: {ServerUrl}", ServerUrl);
            logger.LogInformation("分拣单数据同步接口地址: {BatchOrderSyncUrl}", settings.BatchOrderSyncUrl);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "启动Web服务器失败");
            throw;
        }
    }

    /// <summary>
    /// 停止Web服务器
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isRunning || _host == null)
        {
            logger.LogWarning("Web服务器未运行，无需停止");
            return;
        }

        try
        {
            await _host.StopAsync();
            _host.Dispose();
            _host = null;
            _isRunning = false;
            ServerUrl = null;
            
            logger.LogInformation("Web服务器已停止");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "停止Web服务器失败");
            throw;
        }
    }
}