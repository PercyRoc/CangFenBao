using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Common.Models.Package;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.Rfid;
using Serilog;
using Serilog.Context;

namespace FridFlipMachine.Services;

/// <summary>
///     Frid包裹服务，专门用于FridFlipMachine项目，不依赖于相机服务
/// </summary>
public class FridPackageService : IDisposable
{
    private readonly IFridService _fridService;
    private readonly ISettingsService _settingsService;
    private readonly IDisposable _cleanupSubscription;
    private readonly ConcurrentDictionary<string, DateTime> _processedBarcodes = new();
    private readonly Subject<PackageInfo> _packageSubject = new();
    private bool _isDisposed;

    /// <summary>
    ///     构造函数
    /// </summary>
    public FridPackageService(
        IFridService fridService,
        ISettingsService settingsService)
    {
        _fridService = fridService;
        _settingsService = settingsService;

        // 设置定期清理
        _cleanupSubscription = Observable.Timer(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), Scheduler.Default)
            .Subscribe(_ => CleanupExpiredBarcodes());

        // 订阅Frid标签数据
        _fridService.TagDataReceived += OnFridTagDataReceived;
    }

    /// <summary>
    ///     包裹信息流 (公开给外部订阅)
    /// </summary>
    public IObservable<PackageInfo> PackageStream => _packageSubject.AsObservable();

    /// <summary>
    ///     处理Frid标签数据
    /// </summary>
    private void OnFridTagDataReceived(FridTagData tagData)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(tagData.Epc))
            {
                Log.Debug("接收到空的Frid标签数据，跳过处理");
                return;
            }

            // 检查是否可处理（重复过滤）
            if (!IsBarcodeProcessable(tagData.Epc))
            {
                Log.Information("Frid标签 {Epc} 因重复被过滤", tagData.Epc);
                return;
            }

            // 创建包裹信息
            var package = PackageInfo.Create();
            package.SetBarcode(tagData.Epc);
            package.CreateTime = tagData.ReadTime;
            package.SetStatus(PackageStatus.Processing);

            // 更新已处理条码的时间戳
            _processedBarcodes[tagData.Epc] = DateTime.Now;

            // 发布包裹信息
            _packageSubject.OnNext(package);

            Log.Information("处理Frid标签数据: EPC={Epc}, 天线={Antenna}, 信号强度={Rssi}", 
                tagData.Epc, tagData.AntennaNo, tagData.Rssi);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理Frid标签数据时发生错误");
        }
    }

    /// <summary>
    ///     检查条码是否可处理（重复过滤）
    /// </summary>
    private bool IsBarcodeProcessable(string barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode) || barcode.Equals("NOREAD", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 检查是否在重复时间内
        if (_processedBarcodes.TryGetValue(barcode, out var lastProcessed))
        {
            var timeSinceLastProcessed = DateTime.Now - lastProcessed;
            var repeatTimeMs = 5000; // 5秒重复过滤时间

            if (timeSinceLastProcessed.TotalMilliseconds < repeatTimeMs)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     清理过期的条码记录
    /// </summary>
    private void CleanupExpiredBarcodes()
    {
        try
        {
            var cutoffTime = DateTime.Now.AddMinutes(-10); // 10分钟前的记录
            var expiredBarcodes = _processedBarcodes
                .Where(kvp => kvp.Value < cutoffTime)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var barcode in expiredBarcodes)
            {
                _processedBarcodes.TryRemove(barcode, out _);
            }

            if (expiredBarcodes.Count > 0)
            {
                Log.Debug("清理了 {Count} 个过期的条码记录", expiredBarcodes.Count);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "清理过期条码记录时发生错误");
        }
    }

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
            try
            {
                _fridService.TagDataReceived -= OnFridTagDataReceived;
                _cleanupSubscription?.Dispose();
                _packageSubject?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "释放FridPackageService资源时发生错误");
            }
        }

        _isDisposed = true;
    }
} 