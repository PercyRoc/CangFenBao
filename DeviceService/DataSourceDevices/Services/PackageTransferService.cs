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
    private readonly IDisposable _packageSubscription;
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

        // 订阅包裹流
        _packageSubscription = _cameraService.PackageStream.Subscribe(HandlePackageInfo);
    }

    /// <summary>
    ///     包裹信息流
    /// </summary>
    public IObservable<PackageInfo> PackageStream => _cameraService.PackageStream
        .Where(static package => !string.IsNullOrWhiteSpace(package.Barcode))
        .Where(package => !_cameraSettings.BarcodeRepeatFilterEnabled || IsValidBarcode(package.Barcode))
        .Do(package =>
        {
            // 更新条码处理记录
            _processedBarcodes.AddOrUpdate(
                package.Barcode,
                (package.CreateTime, 1),
                (_, record) => (package.CreateTime, record.Count + 1)
            );

            // 处理包裹信息
            ProcessPackage(package);
        });

    /// <summary>
    ///     释放资源
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
            return;

        _packageSubscription.Dispose();
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
            return _settingsService.LoadSettings<CameraSettings>("Camera");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载相机设置失败，使用默认设置");
            return new CameraSettings();
        }
    }

    /// <summary>
    ///     处理包裹信息
    /// </summary>
    private void HandlePackageInfo(PackageInfo package)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(package.Barcode))
            {
                Log.Warning("收到条码为空的包裹信息");
                return;
            }

            // 检查是否启用条码重复过滤
            if (_cameraSettings.BarcodeRepeatFilterEnabled)
                // 检查条码是否在重复间隔内
                if (!IsValidBarcode(package.Barcode))
                {
                    Log.Debug("条码 {Barcode} 在重复间隔内或超过重复次数，已忽略", package.Barcode);
                    return;
                }

            // 更新条码处理记录
            _processedBarcodes.AddOrUpdate(
                package.Barcode,
                (package.CreateTime, 1),
                (_, record) => (package.CreateTime, record.Count + 1)
            );

            // 处理包裹信息
            ProcessPackage(package);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹信息时发生错误");
        }
    }

    /// <summary>
    ///     检查条码是否有效（不在重复间隔内且未超过重复次数）
    /// </summary>
    private bool IsValidBarcode(string barcode)
    {
        if (!_processedBarcodes.TryGetValue(barcode, out var record)) return true;

        var timeSinceLastProcess = DateTime.Now - record.Time;
        var timeWindowMs = _cameraSettings.RepeatTimeMs;
        var maxRepeatCount = _cameraSettings.RepeatCount;

        // 如果超过时间窗口，重置计数
        if (!(timeSinceLastProcess.TotalMilliseconds > timeWindowMs)) return record.Count < maxRepeatCount;

        _processedBarcodes.TryRemove(barcode, out _);
        return true;
    }

    /// <summary>
    ///     处理包裹信息
    /// </summary>
    private void ProcessPackage(PackageInfo package)
    {
        try
        {
            // 清理过期的条码记录
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

        foreach (var barcode in expiredBarcodes) _processedBarcodes.TryRemove(barcode, out _);
    }
}