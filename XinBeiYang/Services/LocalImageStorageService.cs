using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Serilog;

// 添加对 System.Windows 的引用

namespace XinBeiYang.Services;

/// <summary>
///     实现 IImageStorageService 接口，将图像保存到本地文件系统。
/// </summary>
public class LocalImageStorageService : IImageStorageService, IDisposable
{
    private const double DiskSpaceThreshold = 90.0; // 磁盘空间占用率阈值（百分比）
    private readonly string _baseStoragePath;
    private readonly StaRenderThread _renderThread;
    private const int MaxPixelDimension = 6000; // 分辨率上限，超过则缩放
    private const int MinSaveIntervalMs = 200; // 最小保存间隔（简单限帧）
    private long _lastSaveTicks;
    private bool _disposed;

    public LocalImageStorageService()
    {
        // 默认存储路径：E:/Images，使用Path.GetFullPath标准化路径格式
        _baseStoragePath = Path.GetFullPath("E:/Images");
        Log.Information("本地图像存储路径设置为: {Path}", _baseStoragePath);
        _renderThread = new StaRenderThread();
    }

    /// <summary>
    ///     使用指定的基础路径初始化 LocalImageStorageService 类的新实例。
    /// </summary>
    /// <param name="baseStoragePath">存储图像的根目录。</param>
    public LocalImageStorageService(string baseStoragePath)
    {
        // 使用Path.GetFullPath标准化路径格式，避免混合分隔符问题
        _baseStoragePath = Path.GetFullPath(baseStoragePath);
        Log.Information("本地图像存储路径设置为: {Path}", _baseStoragePath);
        _renderThread = new StaRenderThread();
    }

    /// <inheritdoc />
    public async Task<string?> SaveImageWithWatermarkAsync(
        BitmapSource? image,
        string barcode,
        double weightKg,
        double? lengthCm,
        double? widthCm,
        double? heightCm,
        DateTime createTime)
    {
        if (image == null)
        {
            Log.Warning("尝试保存空图像（带水印），条码为 {Barcode}", barcode);
            return null;
        }

        try
        {
            // 0) 简单帧率限制
            var nowTicks = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastSaveTicks);
            if (nowTicks - last < MinSaveIntervalMs)
            {
                Log.Debug("保存节流: {Delta}ms < {Min}ms，跳过本次保存，条码 {Barcode}", nowTicks - last,
                    MinSaveIntervalMs, barcode);
                return null;
            }
            Interlocked.Exchange(ref _lastSaveTicks, nowTicks);

            // 1) 基本像素校验
            if (image.PixelWidth <= 0 || image.PixelHeight <= 0)
            {
                Log.Warning("图像像素尺寸非法 W={W} H={H}，将不加水印直接保存，条码 {Barcode}", image.PixelWidth,
                    image.PixelHeight, barcode);
                return await SaveImageAsync(image, barcode, createTime);
            }

            // 2) Dispatcher 可用性检查（避免在应用退出阶段调用 UI 渲染）
            var renderDispatcher = _renderThread.Dispatcher ?? Application.Current?.Dispatcher;
            if (renderDispatcher == null || renderDispatcher.HasShutdownStarted || renderDispatcher.HasShutdownFinished)
            {
                Log.Warning("UI Dispatcher 不可用(关闭中或未初始化)，将不加水印直接保存，条码 {Barcode}", barcode);
                var scaledFallback = ScaleIfTooLarge(image);
                var safeFallback = MakeThreadSafeBitmap(scaledFallback);
                Log.Debug("UI Dispatcher 不可用，使用缩放后的安全原图保存: IsFrozen={IsFrozen}", safeFallback.IsFrozen);
                return await SaveImageAsync(safeFallback, barcode, createTime);
            }

            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(barcode)) lines.Add($"条码: {barcode}");
            if (weightKg > 0) lines.Add($"重量: {weightKg:F2} kg");
            if (lengthCm.HasValue && widthCm.HasValue && heightCm.HasValue)
                lines.Add($"体积: {lengthCm.Value:F1}×{widthCm.Value:F1}×{heightCm.Value:F1} cm");
            lines.Add($"时间: {createTime:yyyy-MM-dd HH:mm:ss}");

            // 3) 生成带水印的图像（在UI线程上渲染WPF视觉对象），失败则降级为原图
            var watermarked = await renderDispatcher.InvokeAsync(() =>
            {
                try
                {
                    var input = ScaleIfTooLarge(image);
                    return AddWatermarkTopLeftGreen(input, lines);
                }
                catch (Exception ex)
                {
                    Log.Error(ex,
                        "在 UI 线程渲染水印时失败，将降级保存原图。线程={ThreadId} 像素={W}x{H} 条码={Barcode}",
                        Environment.CurrentManagedThreadId, image.PixelWidth, image.PixelHeight, barcode);
                    return (RenderTargetBitmap?)null;
                }
            });

            if (watermarked == null)
            {
                var scaledFallback = ScaleIfTooLarge(image);
                var safeFallback = MakeThreadSafeBitmap(scaledFallback);
                Log.Debug("水印渲染失败或跳过，使用缩放后的安全原图保存: IsFrozen={IsFrozen}", safeFallback.IsFrozen);
                return await SaveImageAsync(safeFallback, barcode, createTime);
            }

            // 在保存带水印图像前，确保其可跨线程
            var safeWatermarked = MakeThreadSafeBitmap(watermarked);
            Log.Debug("保存带水印图像前冻结检查: IsFrozen={IsFrozen}", safeWatermarked.IsFrozen);
            return await SaveImageAsync(safeWatermarked, barcode, createTime);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存带水印图像时发生错误，条码 {Barcode}", barcode);
            // 策略一：顶层异常也尝试降级保存原图，避免数据丢失
            try
            {
                Log.Warning("尝试在异常后降级保存原始图像。条码={Barcode}", barcode);
                var scaledFallback = ScaleIfTooLarge(image);
                var safeFallback = MakeThreadSafeBitmap(scaledFallback);
                Log.Debug("异常后降级保存原始图像，使用缩放后的安全原图: IsFrozen={IsFrozen}", safeFallback.IsFrozen);
                return await SaveImageAsync(safeFallback, barcode, createTime);
            }
            catch (Exception fallbackEx)
            {
                Log.Error(fallbackEx, "降级保存原始图像也失败了。条码={Barcode}", barcode);
                return null;
            }
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
            try
            {
                _renderThread?.Stop();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "停止图片渲染线程时发生错误");
            }

        _disposed = true;
    }

    ~LocalImageStorageService()
    {
        Dispose(false);
    }

    // 旧方法：基础保存（已由带水印方法取代）。保留内部使用以避免重复代码。
    public async Task<string?> SaveOriginalAsync(BitmapSource? image, string barcode, DateTime createTime)
    {
        if (image == null)
        {
            Log.Warning("尝试保存空图像，条码为 {Barcode}", barcode);
            return null;
        }

        try
        {
            // 检查磁盘空间并清理
            CheckAndCleanupDiskSpace();

            // 创建目录结构：基础路径 / yyyy-MM-dd
            var dateFolderName = createTime.ToString("yyyy-MM-dd");
            var dailyFolderPath = Path.Combine(_baseStoragePath, dateFolderName);

            // 确保目录存在
            if (!Directory.Exists(dailyFolderPath))
            {
                Directory.CreateDirectory(dailyFolderPath);
                Log.Debug("已创建图像存储目录: {Path}", dailyFolderPath);
            }

            // 创建文件名：条码_yyyyMMddHHmmssfff.jpg（使用时间戳确保唯一性）
            // 清理条码中的非法字符（替换无效字符）
            var sanitizedBarcode = SanitizeFileName(barcode);
            // 为WCS路径兼容性，将条码中的逗号替换为连字符
            var filenameBarcode = sanitizedBarcode.Replace(',', '-');
            var timestamp = createTime.ToString("yyyyMMddHHmmssfff");
            var fileName = $"{filenameBarcode}_{timestamp}.jpg";
            var filePath = Path.Combine(dailyFolderPath, fileName);

            // 在进入后台线程前，确保位图跨线程安全
            var safeImage = MakeThreadSafeBitmap(image);
            Log.Debug("保存原图前冻结检查: IsFrozen={IsFrozen}, Type={Type}", safeImage.IsFrozen, safeImage.GetType().FullName);

            // 异步创建文件流
            await using (var fileStream = new FileStream(filePath, FileMode.Create))
            {
                // 将 Encoder 创建、Frame 添加和 Save 操作都放到后台线程执行
                await Task.Run(() =>
                {
                    try
                    {
                        var encoder = new JpegBitmapEncoder(); // 在后台线程创建 Encoder
                        // 使用跨线程安全的位图
                        encoder.Frames.Add(BitmapFrame.Create(safeImage));
                        encoder.Save(fileStream); // 在后台线程保存
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "在后台线程 Task.Run 中 encoder.Save 失败，条码 {Barcode}", barcode);
                        throw; // 重新抛出，让外部 catch 捕获
                    }
                });
            }

            Log.Information("图像保存成功: {FilePath}", filePath);
            return filePath;
        }
        catch (IOException ioEx)
        {
            Log.Error(ioEx, "保存图像时发生IO错误，条码 {Barcode}: {Message}", barcode, ioEx.Message);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存图像时发生错误，条码 {Barcode}: {Message}", barcode, ex.Message);
            return null;
        }
    }

    // 为内部调用保留旧名，避免大量改动
    private Task<string?> SaveImageAsync(BitmapSource? image, string barcode, DateTime createTime)
    {
        return SaveOriginalAsync(image, barcode, createTime);
    }

    private static RenderTargetBitmap? AddWatermarkTopLeftGreen(BitmapSource original, IList<string> lines)
    {
        try
        {
            if (original is { IsFrozen: false, CanFreeze: true })
                try
                {
                    original.Freeze();
                }
                catch
                {
                    // ignored
                }

            var pixelWidth = original.PixelWidth;
            var pixelHeight = original.PixelHeight;

            if (pixelWidth <= 0 || pixelHeight <= 0)
            {
                Log.Warning("水印渲染跳过：像素尺寸非法 {W}x{H}", pixelWidth, pixelHeight);
                return null;
            }

            // 若超过上限则缩放目标输出尺寸
            var scale = 1.0;
            if (pixelWidth > MaxPixelDimension || pixelHeight > MaxPixelDimension)
            {
                scale = Math.Min((double)MaxPixelDimension / pixelWidth, (double)MaxPixelDimension / pixelHeight);
                Log.Information("图像缩放以限制分辨率: {W}x{H} -> scale {Scale:F3}", pixelWidth, pixelHeight, scale);
            }
            var targetWidth = Math.Max(1, (int)Math.Round(pixelWidth * scale));
            var targetHeight = Math.Max(1, (int)Math.Round(pixelHeight * scale));

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                // 画布按目标尺寸绘制，WPF 会在绘制时做缩放
                dc.DrawImage(original, new Rect(0, 0, targetWidth, targetHeight));

                var typeface = new Typeface(new FontFamily("Microsoft YaHei"), FontStyles.Normal, FontWeights.Bold,
                    FontStretches.Normal);
                var fontSize = Math.Max(14.0, targetWidth / 60.0);
                var textBrush = new SolidColorBrush(Colors.Lime);
                textBrush.Freeze();

                var padding = 10.0;
                var lineHeight = fontSize * 1.2;
                var y = padding;

                foreach (var line in lines)
                {
                    var formatted = new FormattedText(
                        line,
                        CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        typeface,
                        fontSize,
                        textBrush,
                        1.0);

                    var bgBrush = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0));
                    bgBrush.Freeze();
                    var rect = new Rect(padding - 2, y - 2, formatted.Width + 4, formatted.Height + 4);
                    dc.DrawRectangle(bgBrush, null, rect);
                    dc.DrawText(formatted, new Point(padding, y));
                    y += lineHeight;
                }
            }

            var rtb = new RenderTargetBitmap(targetWidth, targetHeight, original.DpiX, original.DpiY, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "RenderTargetBitmap.Render 失败，跳过水印渲染");
            return null;
        }
    }

    private static BitmapSource ScaleIfTooLarge(BitmapSource source)
    {
        try
        {
            var w = source.PixelWidth;
            var h = source.PixelHeight;
            if (w <= MaxPixelDimension && h <= MaxPixelDimension)
                return source;

            var scale = Math.Min((double)MaxPixelDimension / w, (double)MaxPixelDimension / h);
            var transform = new ScaleTransform(scale, scale);
            var tb = new TransformedBitmap(source, transform);
            if (tb.CanFreeze)
                tb.Freeze();
            return tb;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ScaleIfTooLarge 失败，返回原图");
            return source;
        }
    }

    /// <summary>
    /// 独立 STA 渲染线程，承载 WPF 绘制以避免与 UI 线程耦合。
    /// </summary>
    private sealed class StaRenderThread
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _started = new(false);
        public Dispatcher? Dispatcher { get; private set; }

        public StaRenderThread()
        {
            _thread = new Thread(ThreadStart)
            {
                IsBackground = true,
                Name = "ImageRenderSTA",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            // 等待 Dispatcher 建立
            _started.Wait(TimeSpan.FromSeconds(3));
        }

        private void ThreadStart()
        {
            try
            {
                Dispatcher = Dispatcher.CurrentDispatcher;
                // 策略二：为渲染线程的 Dispatcher 增加未处理异常处理，防止线程因异常终止
                Dispatcher.UnhandledException += static (s, e) =>
                {
                    Log.Error(e.Exception, "STA 渲染线程内部发生未处理的异常!");
                    e.Handled = true; // 吞掉，保持线程存活
                };
                _started.Set();
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "STA 渲染线程运行失败");
            }
        }

        public void Stop()
        {
            try
            {
                var dispatcher = Dispatcher;
                if (dispatcher != null && !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "关闭 STA 渲染线程时发生警告");
            }
        }
    }

    /// <summary>
    ///     检查磁盘空间并清理最早的数据
    /// </summary>
    private void CheckAndCleanupDiskSpace()
    {
        try
        {
            var driveInfo = new DriveInfo(Path.GetPathRoot(_baseStoragePath)!);
            var diskSpaceUsedPercentage = 100 - (double)driveInfo.AvailableFreeSpace / driveInfo.TotalSize * 100;

            if (diskSpaceUsedPercentage >= DiskSpaceThreshold)
            {
                Log.Warning("磁盘空间使用率超过 {Threshold}%，当前使用率: {UsedPercentage}%，开始清理最早的数据",
                    DiskSpaceThreshold, diskSpaceUsedPercentage);

                // 获取所有日期文件夹并按日期排序
                var dateFolders = Directory.GetDirectories(_baseStoragePath)
                    .Select(d => new DirectoryInfo(d))
                    .OrderBy(d => d.CreationTime)
                    .ToList();

                if (dateFolders.Count != 0)
                {
                    // 删除最早的文件夹
                    var oldestFolder = dateFolders.First();
                    try
                    {
                        Directory.Delete(oldestFolder.FullName, true);
                        Log.Information("已删除最早的图像文件夹: {Folder}", oldestFolder.FullName);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "删除文件夹失败: {Folder}", oldestFolder.FullName);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "检查磁盘空间时发生错误");
        }
    }

    /// <summary>
    ///     移除或替换文件名中的非法字符。
    /// </summary>
    private static string SanitizeFileName(string fileName)
    {
        // 移除Windows文件名中的非法字符
        // 如有需要可以添加更具体的清理规则
        return Path.GetInvalidFileNameChars().Aggregate(fileName, (current, c) => current.Replace(c.ToString(), "_"));
    }

    // 保证跨线程安全使用的位图（避免 Handle is not initialized）
    private static BitmapSource MakeThreadSafeBitmap(BitmapSource source)
    {
        // 已冻结则直接使用
        if (source is { IsFrozen: true }) return source;

        try
        {
            if (source.CanFreeze)
            {
                source.Freeze();
                return source;
            }

            // 克隆后再尝试冻结
            var cloned = source.Clone();
            if (cloned.CanFreeze) cloned.Freeze();
            return cloned;
        }
        catch
        {
            try
            {
                // 兜底：转换为常用像素格式的包装，一般可冻结
                var safe = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
                if (safe.CanFreeze) safe.Freeze();
                return safe;
            }
            catch
            {
                // 最后兜底：仍返回原图（可能不跨线程安全），让调用方捕获异常
                return source;
            }
        }
    }
}