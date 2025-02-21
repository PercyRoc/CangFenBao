using Microsoft.Extensions.Hosting;
using Presentation_CommonLibrary.Services;
using CommonLibrary.Models.Settings.Weight;
using CommonLibrary.Services;
using Serilog;

namespace DeviceService.Weight;

/// <summary>
///     重量称启动服务
/// </summary>
public class WeightStartupService : IHostedService
{
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private IWeightService? _weightService;

    /// <summary>
    ///     构造函数
    /// </summary>
    public WeightStartupService(
        INotificationService notificationService,
        ISettingsService settingsService)
    {
        _notificationService = notificationService;
        _settingsService = settingsService;
    }

    /// <summary>
    ///     启动服务
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Log.Information("Starting weight scale service...");
            var weight = GetWeightService();

            // Load configuration
            Log.Debug("Loading weight scale configuration...");
            var config = _settingsService.LoadConfiguration<WeightSettings>();
            if (config == null)
            {
                const string message = "Failed to load weight scale configuration";
                Log.Warning(message);
                _notificationService.ShowError(message, "Weight Scale Service Error");
                return;
            }

            // Update configuration
            Log.Debug("Updating weight scale configuration...");
            await weight.UpdateConfigurationAsync(config);

            // Start service
            if (!await weight.StartAsync(cancellationToken))
            {
                const string message = "Failed to start weight scale service";
                Log.Warning(message);
                _notificationService.ShowError(message, "Weight Scale Service Error");
            }
            else
            {
                Log.Information("Weight scale service started successfully");
                _notificationService.ShowSuccess("Weight scale service started successfully");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动重量称服务时发生错误");
            _notificationService.ShowError(ex.Message, "重量称服务错误");
        }
    }

    /// <summary>
    ///     停止服务
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            Log.Information("正在停止重量称服务...");
            if (_weightService != null)
            {
                await _weightService.StopAsync();
                await _weightService.DisposeAsync();
                _weightService = null;
            }
            Log.Information("重量称服务已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止重量称服务时发生错误");
        }
        finally
        {
            _initLock.Dispose();
        }
    }

    /// <summary>
    ///     获取重量称服务实例
    /// </summary>
    public IWeightService GetWeightService()
    {
        _initLock.Wait();
        try
        {
            return _weightService ??= new SerialPortWeightService();
        }
        finally
        {
            _initLock.Release();
        }
    }
} 