using System.Collections.Concurrent;
using CommonLibrary.Models;
using CommonLibrary.Models.Settings.Camera;
using CommonLibrary.Services;
using DeviceService.Camera;
using Serilog;

namespace DeviceService;

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
        _cameraService.OnPackageInfo += HandlePackageInfo;
    }

    /// <summary>
    ///     释放资源
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
            return;

        _cameraService.OnPackageInfo -= HandlePackageInfo;
        _processedBarcodes.Clear();
        _isDisposed = true;

        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     包裹信息事件
    /// </summary>
    public event Action<PackageInfo>? OnPackageInfo;

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
                (package.CreatedAt, 1),
                (_, record) => (package.CreatedAt, record.Count + 1)
            );

            // 处理包裹信息
            ProcessPackage(package);

            // 发布包裹信息
            OnPackageInfo?.Invoke(package);
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
        if (timeSinceLastProcess.TotalMilliseconds > timeWindowMs)
        {
            _processedBarcodes.TryRemove(barcode, out _);
            return true;
        }

        // 检查是否超过重复次数
        return record.Count < maxRepeatCount;
    }

    /// <summary>
    ///     处理包裹信息
    /// </summary>
    private void ProcessPackage(PackageInfo package)
    {
        try
        {
            // 验证包裹数据
            ValidatePackage(package);

            // 清理过期的条码记录
            CleanupExpiredBarcodes();

            // TODO: 添加其他处理逻辑
            // 例如：数据转换、图像处理、数据存储等
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹 {Barcode} 时发生错误", package.Barcode);
        }
    }

    /// <summary>
    ///     验证包裹数据
    /// </summary>
    private static void ValidatePackage(PackageInfo package)
    {
        if (package.Image == null) Log.Warning("包裹 {Barcode} 缺少图像数据", package.Barcode);

        if (package.Weight <= 0) Log.Warning("包裹 {Barcode} 重量异常: {Weight}", package.Barcode, package.Weight);

        if (package.Length <= 0 || package.Width <= 0 || package.Height <= 0)
            Log.Warning("包裹 {Barcode} 尺寸异常: {Length}x{Width}x{Height}",
                package.Barcode, package.Length, package.Width, package.Height);
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
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var barcode in expiredBarcodes) _processedBarcodes.TryRemove(barcode, out _);
    }
}