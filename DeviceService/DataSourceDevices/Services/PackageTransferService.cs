using System.Collections.Concurrent;
using System.Reactive.Linq;
using Common.Models.Package;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Camera.Models.Camera;
using Serilog;

namespace DeviceService.DataSourceDevices.Services;

/// <summary>
///     包裹中转服务
/// </summary>
public class PackageTransferService : IDisposable
{
    private readonly ICameraService _cameraService;
    private readonly CameraSettings _cameraSettings;
    private readonly ConcurrentDictionary<string, (DateTime Time, int Count)> _processedBarcodes = new();
    private readonly ISettingsService _settingsService;
    private bool _isDisposed;

    /// <summary>
    ///     构造函数
    /// </summary>
    /// <param name="cameraService">相机服务</param>
    /// <param name="settingsService">设置服务</param>
    public PackageTransferService(ICameraService cameraService, ISettingsService settingsService)
    {
        _cameraService = cameraService;
        _settingsService = settingsService;
        _cameraSettings = LoadCameraSettings();
    }

    /// <summary>
    ///     包裹信息流 (公开给外部订阅，包含过滤逻辑)
    /// </summary>
    public IObservable<PackageInfo> PackageStream => _cameraService.PackageStream
        .Where(static package => !string.IsNullOrWhiteSpace(package.Barcode))
        .Where(package =>
        {
            if (!_cameraSettings.BarcodeRepeatFilterEnabled) return true;

            return IsValidBarcode(package.Barcode);
        })
        .Do(package =>
        {
            _processedBarcodes.AddOrUpdate(
                package.Barcode,
                (package.CreateTime, 1),
                (_, _) => (package.CreateTime, 1)
            );

            ProcessPackage(package);
        });

    /// <summary>
    ///     释放资源
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
            return;

        _processedBarcodes.Clear();
        _isDisposed = true;

        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     加载相机设置
    /// </summary>
    private CameraSettings LoadCameraSettings()
    {
        try
        {
            return _settingsService.LoadSettings<CameraSettings>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载相机设置失败，使用默认设置");
            return new CameraSettings();
        }
    }

    /// <summary>
    ///     检查条码是否有效（是否应该发布）
    /// </summary>
    private bool IsValidBarcode(string barcode)
    {
        if (!_processedBarcodes.TryGetValue(barcode, out var record))
        {
            return true;
        }

        var timeSinceLastProcess = DateTime.Now - record.Time;
        var timeWindowMs = _cameraSettings.RepeatTimeMs;

        if (timeSinceLastProcess.TotalMilliseconds <= timeWindowMs)
        {
            return false;
        }
        else
        {
            _processedBarcodes.TryRemove(barcode, out _);
            return true;
        }
    }

    /// <summary>
    ///     处理包裹信息 (现在只包含清理逻辑)
    /// </summary>
    private void ProcessPackage(PackageInfo package)
    {
        try
        {
            CleanupExpiredBarcodes();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹 {Barcode} 时发生错误", package.Barcode);
        }
    }

    /// <summary>
    ///     清理过期的条码记录
    /// </summary>
    private void CleanupExpiredBarcodes()
    {
        var now = DateTime.Now;
        var timeWindowMs = _cameraSettings.RepeatTimeMs;
        var expiredBarcodes = _processedBarcodes
            .Where(kvp => (now - kvp.Value.Time).TotalMilliseconds > timeWindowMs)
            .Select(static kvp => kvp.Key)
            .ToList();

        if (expiredBarcodes.Count > 0)
        {
            Log.Debug("清理 {Count} 个过期条码记录", expiredBarcodes.Count);
            foreach (var barcode in expiredBarcodes) _processedBarcodes.TryRemove(barcode, out _);
        }
    }
}