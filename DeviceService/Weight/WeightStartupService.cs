using Microsoft.Extensions.Hosting;
using Presentation_CommonLibrary.Services;
using Serilog;

namespace DeviceService.Weight;

/// <summary>
///     重量称启动服务
/// </summary>
public class WeightStartupService : IHostedService
{
    private readonly IDialogService _dialogService;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private IWeightService? _weightService;

    /// <summary>
    ///     构造函数
    /// </summary>
    public WeightStartupService(IDialogService dialogService)
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
            Log.Information("正在启动重量称服务...");
            var weight = GetWeightService();

            if (!await weight.StartAsync(cancellationToken))
            {
                const string message = "重量称服务启动失败";
                Log.Warning(message);
                await _dialogService.ShowErrorAsync(message, "重量称服务错误");
            }
            else
            {
                Log.Information("重量称服务启动成功");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动重量称服务时发生错误");
            await _dialogService.ShowErrorAsync(ex.Message, "重量称服务错误");
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