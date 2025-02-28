using Microsoft.Extensions.Hosting;
using Presentation_CommonLibrary.Services;
using Serilog;

namespace DeviceService.Scanner;

/// <summary>
///     扫码枪启动服务
/// </summary>
public class ScannerStartupService : IHostedService
{
    private readonly IDialogService _dialogService;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private IScannerService? _scannerService;

    /// <summary>
    ///     构造函数
    /// </summary>
    public ScannerStartupService(IDialogService dialogService)
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
            Log.Information("正在启动扫码枪服务...");
            var scanner = GetScannerService();

            if (!scanner.Start())
            {
                const string message = "扫码枪服务启动失败";
                Log.Warning(message);
                await _dialogService.ShowErrorAsync(message, "扫码枪服务错误");
            }
            else
            {
                Log.Information("扫码枪服务启动成功");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动扫码枪服务时发生错误");
            await _dialogService.ShowErrorAsync(ex.Message, "扫码枪服务错误");
        }
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