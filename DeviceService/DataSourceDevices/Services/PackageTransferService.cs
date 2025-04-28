using System.Collections.Concurrent;
using System.Reactive.Linq;
using Common.Models.Package;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Camera.Models.Camera;
using Serilog;
using System.Reactive.Concurrency; // Add for ObserveOn
using Serilog.Context; // 添加 Serilog.Context 命名空间

namespace DeviceService.DataSourceDevices.Services;

/// <summary>
///     包裹中转服务，包含条码过滤和图像保存逻辑
/// </summary>
public class PackageTransferService : IDisposable
{
    private readonly ICameraService _cameraService;
    private readonly CameraSettings _cameraSettings;
    private readonly IImageSavingService _imageSavingService; // 注入图像保存服务
    private readonly ConcurrentDictionary<string, DateTime> _processedBarcodes = new(); // 简化的字典值
    private readonly ISettingsService _settingsService;
    private bool _isDisposed;
    private readonly IDisposable _cleanupSubscription;

    /// <summary>
    ///     构造函数
    /// </summary>
    public PackageTransferService(
        ICameraService cameraService,
        ISettingsService settingsService,
        IImageSavingService imageSavingService)
    {
        _cameraService = cameraService;
        _settingsService = settingsService;
        _imageSavingService = imageSavingService;
        _cameraSettings = LoadCameraSettings();

        // 设置定期清理
        _cleanupSubscription = Observable.Timer(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), Scheduler.Default)
                                      .Subscribe(_ => CleanupExpiredBarcodes());
    }

    /// <summary>
    ///     包裹信息流 (公开给外部订阅，包含过滤和图像保存逻辑)
    /// </summary>
    public IObservable<PackageInfo> PackageStream => _cameraService.PackageStream
        .Where(package =>
        { // 第一个过滤: 确保有条码
            var hasBarcode = !string.IsNullOrWhiteSpace(package.Barcode);
            if (!hasBarcode) Log.Verbose("包裹 {Index} 因缺少条码被过滤.", package.Index);
            return hasBarcode;
        })
        .Select(package => (Package: package, IsProcessable: IsBarcodeProcessable(package.Barcode))) // 修复: 传递 package.Barcode 而不是 package
        .Do(tuple =>
        { // 记录过滤结果
            if (!tuple.IsProcessable)
            {
                // 在这里记录过滤掉的信息, 因为 LogContext 尚未应用
                var packageContext = $"[包裹{tuple.Package.Index}|{tuple.Package.Barcode}]";
                Log.Information("{PackageContext} 因重复 ({RepeatTimeMs}ms 内) 被过滤.",
                    packageContext, _cameraSettings.RepeatTimeMs);
            }
        })
        .Where(tuple => tuple.IsProcessable) // 第二个过滤: 基于重复性检查结果
        .Select(tuple => tuple.Package) // 只选择通过过滤的包裹
        .Select(package => // 使用 SelectMany 或类似操作引入异步可能更复杂，暂时用 Select + Task.Run
        {
            // --- 开始应用日志上下文 ---
            var packageContext = $"[包裹{package.Index}|{package.Barcode}]";
            using (LogContext.PushProperty("PackageContext", packageContext))
            {
                Log.Information("已通过过滤器, 开始处理图像和保存.");

                // 更新已处理条码的时间戳 (移到这里确保只对通过的包裹更新)
                if (package.Barcode != "NOREAD" && _cameraSettings.BarcodeRepeatFilterEnabled)
                {
                    _processedBarcodes[package.Barcode] = DateTime.Now;
                }

                var originalImage = package.Image; // 获取原始图像引用
                string? generatedPath = null;
                var triggerTime = package.TriggerTimestamp;
                bool imageSavingEnabled = _cameraSettings.EnableImageSaving;

                try
                {
                    // 1. 生成潜在的保存路径 (即使保存被禁用或无图像也要生成)
                    if (imageSavingEnabled)
                    {
                        generatedPath = _imageSavingService.GenerateImagePath(package.Barcode, triggerTime);
                        if (generatedPath != null) Log.Debug("生成潜在图像路径: {Path}", generatedPath);
                        else Log.Warning("无法生成图像路径 (可能未配置 ImageSavePath?).");
                    }
                    else
                    {
                        Log.Debug("图像保存功能已禁用, 跳过路径生成和保存.");
                    }

                    // 2. 更新 PackageInfo 的 Image 和 ImagePath
                    // 无论后续保存是否成功, 都应将原始图像(如果存在)和生成的路径(如果存在)设置回包裹
                    package.SetImage(originalImage, generatedPath);
                    Log.Debug("已更新 PackageInfo 的 ImagePath (可能为 null).");

                    // 3. 如果图像保存已启用且有图像，则触发异步保存
                    if (imageSavingEnabled && originalImage != null)
                    {
                        // 为后台任务克隆并冻结图像
                        var cloneForSave = originalImage.Clone();
                        cloneForSave.Freeze();

                        Log.Debug("准备启动后台任务保存图像.");
                        // 捕获上下文信息用于后台任务
                        var barcodeForTask = package.Barcode;
                        var indexForTask = package.Index;
                        var contextForTask = packageContext; // 捕获上下文

                        _ = Task.Run(async () =>
                        {
                            // 在后台任务中恢复日志上下文
                            using (LogContext.PushProperty("PackageContext", contextForTask))
                            {
                                Log.Debug("后台保存任务开始.");
                                string? actualSavedPath = null;
                                try
                                {
                                    actualSavedPath = await _imageSavingService.SaveImageAsync(cloneForSave, barcodeForTask, triggerTime);

                                    if (actualSavedPath != null)
                                    {
                                        Log.Information("后台图像保存成功, 路径: {ImagePath}", actualSavedPath);
                                    }
                                    else
                                    {
                                        // SaveImageAsync 返回 null 的原因已经在该服务内部记录 (例如禁用, 路径错误等)
                                        Log.Warning("后台图像保存任务返回 null (可能已禁用或失败).");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log.Error(ex, "后台图像保存任务发生异常.");
                                }
                                // cloneForSave 在此任务完成后超出作用域, 可被 GC 回收.
                                Log.Debug("后台保存任务结束.");
                            }
                        });
                    }
                    else if (imageSavingEnabled && originalImage == null)
                    {
                        Log.Warning("图像保存已启用, 但包裹中无可用图像.");
                    }
                    // 如果 imageSavingEnabled == false, 此前已记录日志

                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理图像路径或触发保存时发生主流程错误.");
                    // 确保即使出错，图像和路径（如果已生成）仍在 packageInfo 中
                    package.SetImage(originalImage, generatedPath);
                }

                Log.Debug("图像处理和保存流程完成 (保存任务可能仍在后台运行).");
                return package; // 返回更新后的包裹

            } // --- 日志上下文结束 ---
        });

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
            _cleanupSubscription.Dispose(); // 释放定时器订阅
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
        // 如果禁用过滤或条码是 NOREAD，则总是可处理
        if (!_cameraSettings.BarcodeRepeatFilterEnabled || barcode == "NOREAD") return true;

        if (!_processedBarcodes.TryGetValue(barcode, out var lastProcessedTime))
        {
            // 最近未见过此条码, 处理它
            return true;
        }

        var timeSinceLastProcess = DateTime.Now - lastProcessedTime;
        var timeWindow = TimeSpan.FromMilliseconds(_cameraSettings.RepeatTimeMs);

        // 仅当时间窗口内处理过才返回 false (被过滤)
        return timeSinceLastProcess > timeWindow;
        // 过滤日志移至 Do 操作符中，以便在上下文中记录
        // Log.Debug("过滤掉在 {TimeWindow} 毫秒内重复的条码 {Barcode}.", barcode, _cameraSettings.RepeatTimeMs);
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

        if (expiredBarcodes.Count <= 0) return;
        Log.Debug("清理 {Count} 条过期的条码记录.", expiredBarcodes.Count);
        foreach (var barcode in expiredBarcodes)
        {
            _processedBarcodes.TryRemove(barcode, out _);
        }
    }
}