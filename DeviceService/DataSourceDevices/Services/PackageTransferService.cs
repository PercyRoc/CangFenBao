using System.Collections.Concurrent;
using System.Reactive.Linq;
using Common.Models.Package;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Camera.Models.Camera;
using Serilog;
using System.Reactive.Concurrency; // Add for ObserveOn
using System.Windows.Media.Imaging;

namespace DeviceService.DataSourceDevices.Services;

/// <summary>
///     包裹中转服务，包含条码过滤和图像保存逻辑
/// </summary>
public class PackageTransferService : IDisposable
{
    private readonly ICameraService _cameraService;
    private readonly CameraSettings _cameraSettings;
    private readonly IImageSavingService _imageSavingService; // Inject image saving service
    private readonly ConcurrentDictionary<string, DateTime> _processedBarcodes = new(); // Simplified dictionary value
    private readonly ISettingsService _settingsService;
    private bool _isDisposed;
    private readonly IDisposable _cleanupSubscription;

    /// <summary>
    ///     构造函数
    /// </summary>
    public PackageTransferService(
        ICameraService cameraService,
        ISettingsService settingsService,
        IImageSavingService imageSavingService) // Add dependency
    {
        _cameraService = cameraService;
        _settingsService = settingsService;
        _imageSavingService = imageSavingService; // Store dependency
        _cameraSettings = LoadCameraSettings();

        // Setup periodic cleanup instead of on every package
        _cleanupSubscription = Observable.Timer(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), Scheduler.Default)
                                      .Subscribe(_ => CleanupExpiredBarcodes());
    }

    /// <summary>
    ///     包裹信息流 (公开给外部订阅，包含过滤和图像保存逻辑)
    /// </summary>
    public IObservable<PackageInfo> PackageStream => _cameraService.PackageStream
        .Where(static package => !string.IsNullOrWhiteSpace(package.Barcode)) // Filter out packages without barcode
        .Where(package => // Apply barcode repeat filter if enabled
        {
            if (!_cameraSettings.BarcodeRepeatFilterEnabled || package.Barcode == "NOREAD") return true;
            return IsBarcodeProcessable(package.Barcode);
        })
        .Do(package => // 在异步操作之前更新已处理条码的时间戳
        {
            if (package.Barcode != "NOREAD" && _cameraSettings.BarcodeRepeatFilterEnabled)
            {
                _processedBarcodes[package.Barcode] = DateTime.Now; // 更新最后处理时间
            }
            // 记录通过过滤并进入图像保存流程的包裹
            Log.Verbose("包裹 {Index} ({Barcode}) 已通过过滤器, 准备保存图像.", package.Index, package.Barcode);
        })
        .Select(package => // 生成路径, 更新包裹, 然后触发异步保存 (同步转换)
        {
            var originalImage = package.Image;
            string? generatedPath = null;

            try
            {
                var triggerTime = package.TriggerTimestamp;
                // 1. 首先生成潜在的保存路径
                generatedPath = _imageSavingService.GenerateImagePath(package.Barcode, triggerTime);

                // 记录路径生成结果
                if (generatedPath != null) Log.Debug("为包裹 {Index} ({Barcode}) 生成潜在图像路径: {Path}", package.Index, package.Barcode, generatedPath);
                else Log.Warning("无法为包裹 {Index} ({Barcode}) 生成图像路径. 路径将不会被设置.", package.Index, package.Barcode);

                if (originalImage != null)
                {
                    // 2. 克隆一次，专用于后台保存任务
                    var cloneForSave = originalImage.Clone();
                    cloneForSave.Freeze(); // 冻结图像使其可以安全地跨线程访问

                    // 3. 使用原始图像和生成的路径更新 PackageInfo
                    package.SetImage(originalImage, generatedPath);

                    // 4. 异步触发保存操作 (触发即忘)
                    _ = Task.Run(async () =>
                    {
                        // 将克隆副本传递给保存服务
                        string? actualSavedPath = await _imageSavingService.SaveImageAsync(cloneForSave, package.Barcode, triggerTime);
                        if (actualSavedPath != null)
                        {
                            Log.Information("包裹 {Index} 的后台图像保存完成, 路径: {ImagePath}", package.Index, actualSavedPath);
                            // cloneForSave 在此任务完成后超出作用域, 可被 GC 回收.
                        }
                        else
                        {
                            Log.Warning("包裹 {Index} ({Barcode}) 的后台图像保存失败或被跳过.", package.Index, package.Barcode);
                            // cloneForSave 在此任务完成后超出作用域, 可被 GC 回收.
                        }
                    });
                }
                else
                {
                    // 包裹信息中没有可用图像
                    Log.Warning("包裹 {Index} ({Barcode}) 中无可用图像进行保存.", package.Index, package.Barcode);
                    // 使用 null 图像但包含生成的路径来更新 PackageInfo
                    package.SetImage(originalImage, generatedPath);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "在为包裹 {Index} ({Barcode}) 生成图像路径或触发保存时发生错误.", package.Index, package.Barcode);
                // 发生错误时, 尝试设置原始图像(如果可用)和生成的路径(如果可用)
                // 这维持了在包裹实例中保留原始图像的原则.
                package.SetImage(originalImage, generatedPath);
            }
            return package; // 立即返回包裹 (Select 需要返回 T)
        });
    // Removed ProcessPackage method call

    /// <summary>
    ///     释放资源
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (disposing)
        {
            _cleanupSubscription?.Dispose(); // 释放定时器订阅
            _processedBarcodes.Clear();
            // 如果有其他托管资源也在此处释放
        }

        // 如果有非托管资源在此处释放

        _isDisposed = true;
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
            Log.Error(ex, "加载 CameraSettings 失败, 将使用默认设置.");
            return new CameraSettings(); // 出错时返回默认设置
        }
    }

    /// <summary>
    ///     检查条码是否应该被处理 (基于重复过滤设置)
    /// </summary>
    private bool IsBarcodeProcessable(string barcode)
    {
        if (!_processedBarcodes.TryGetValue(barcode, out var lastProcessedTime))
        {
            // 最近未见过此条码, 处理它
            return true;
        }

        var timeSinceLastProcess = DateTime.Now - lastProcessedTime;
        var timeWindow = TimeSpan.FromMilliseconds(_cameraSettings.RepeatTimeMs);

        if (timeSinceLastProcess <= timeWindow)
        {
            // 过滤掉在 {TimeWindow} 毫秒内重复的条码 {Barcode}
            Log.Debug("过滤掉在 {TimeWindow} 毫秒内重复的条码 {Barcode}.", barcode, _cameraSettings.RepeatTimeMs);
            return false;
        }
        return true;
    }
    /// <summary>
    ///     定期清理过期的条码记录
    /// </summary>
    private void CleanupExpiredBarcodes()
    {
        if (_processedBarcodes.IsEmpty) return;

        var now = DateTime.Now;
        // 使用稍大的窗口进行清理以避免竞争条件
        var cleanupWindow = TimeSpan.FromMilliseconds(_cameraSettings.RepeatTimeMs * 1.5);

        var expiredBarcodes = _processedBarcodes
            .Where(kvp => (now - kvp.Value) > cleanupWindow)
            .Select(kvp => kvp.Key)
            .ToList();

        if (expiredBarcodes.Count > 0)
        {
            Log.Debug("清理 {Count} 条过期的条码记录.", expiredBarcodes.Count);
            foreach (var barcode in expiredBarcodes)
            {
                _processedBarcodes.TryRemove(barcode, out _);
            }
        }
    }
}