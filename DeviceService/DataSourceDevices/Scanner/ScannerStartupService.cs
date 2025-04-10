using Microsoft.Extensions.Hosting;
using Serilog;

namespace DeviceService.DataSourceDevices.Scanner;

/// <summary>
///     扫码枪启动服务
/// </summary>
/// <remarks>
///     构造函数
/// </remarks>
public class ScannerStartupService : IHostedService
{
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private UsbScannerService? _scannerService;

    /// <summary>
    ///     启动服务
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Log.Information("正在启动扫码枪服务...");
            var scanner = GetScannerService();

            if (!scanner.Start())
            {
                const string message = "扫码枪服务启动失败";
                Log.Warning(message);
            }
            else
            {
                Log.Information("扫码枪服务启动成功");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动扫码枪服务时发生错误");
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
            Log.Information("正在停止扫码枪服务...");
            _scannerService?.Stop();
            _scannerService?.Dispose();
            _scannerService = null;
            Log.Information("扫码枪服务已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止扫码枪服务时发生错误");
        }
        finally
        {
            _initLock.Dispose();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///     获取扫码枪服务实例
    /// </summary>
    public IScannerService GetScannerService()
    {
        _initLock.Wait();
        try
        {
            return _scannerService ??= new UsbScannerService();
        }
        finally
        {
            _initLock.Release();
        }
    }
}