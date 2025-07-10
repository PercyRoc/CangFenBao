using System.Windows.Media.Imaging;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.Camera.Models.Camera;
using DeviceService.DataSourceDevices.Camera.Models.Camera.Enums;
using Serilog;
using System.Windows.Media;
using System.Windows;
using Common.Models.Package;

namespace DeviceService.DataSourceDevices.Services;

/// <summary>
///     负责根据配置将图像保存到本地磁盘的服务。
/// </summary>
public class ImageSavingService(ISettingsService settingsService) : IImageSavingService
{
    private readonly ISettingsService _settingsService =
        settingsService ?? throw new ArgumentNullException(nameof(settingsService));

    /// <inheritdoc />
    public string? GenerateImagePath(string? barcode, DateTime timestamp)
    {
        var cameraSettings = _settingsService.LoadSettings<CameraSettings>();

        // 检查是否是NOREAD条码，如果是则无条件生成路径
        var isNoRead = string.IsNullOrWhiteSpace(barcode) || barcode.Equals("NOREAD", StringComparison.OrdinalIgnoreCase);
        if (isNoRead)
        {
            return BuildNoreadImagePath(cameraSettings, timestamp);
        }

        return !cameraSettings.EnableImageSaving
            ? null
            : // 保存已禁用
            BuildImagePath(cameraSettings, barcode, timestamp);
    }

    /// <inheritdoc />
    public async Task<string?> SaveImageAsync(BitmapSource? image, string? barcode, DateTime timestamp)
    {
        var cameraSettings = _settingsService.LoadSettings<CameraSettings>();

        // 检查是否是NOREAD条码
        var isNoRead = string.IsNullOrWhiteSpace(barcode) || barcode.Equals("NOREAD", StringComparison.OrdinalIgnoreCase);

        // 如果不是NOREAD条码且图像保存功能关闭，则不保存
        if (!isNoRead && !cameraSettings.EnableImageSaving)
        {
            return null;
        }

        // 如果没有提供图像，则不保存
        if (image == null)
        {
            return null;
        }

        try
        {
            var fullPath =
                // NOREAD条码使用专门的路径构建逻辑
                isNoRead ? BuildNoreadImagePath(cameraSettings, timestamp) :
                    // 普通条码使用原有的路径构建逻辑
                    BuildImagePath(cameraSettings, barcode, timestamp);

            if (string.IsNullOrEmpty(fullPath))
            {
                Log.Warning("无法为条码 {Barcode} (时间戳 {Timestamp}) 生成图像路径.", barcode, timestamp);
                return null;
            }

            // 保存前确保目录存在
            var directoryPath = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
            else
            {
                Log.Warning("无法确定路径 {FullPath} 的目录.", fullPath);
                return null;
            }

            // 获取所需的图像格式，以便在 Task.Run 内部使用
            var imageFormat = cameraSettings.ImageFormat;

            // 确保传入的图像已冻结（虽然调用者应该已经做了）
            if (!image.IsFrozen)
            {
                Log.Warning("图像保存服务收到一个未冻结的图像 (条码: {Barcode})。保存可能失败。", barcode);
                // 注意：在这里尝试冻结通常是无效的，因为它可能不在正确的线程上。
                // 依赖调用者（PackageTransferService）正确地冻结它。
            }

            await Task.Run(() => // 在此后台线程执行所有WPF对象交互和保存
            {
                var encoder = GetEncoder(imageFormat); // 在这里创建编码器
                var frame = BitmapFrame.Create(image); // 在这里创建帧 (使用已冻结的图像)
                encoder.Frames.Add(frame);

                using var fileStream = new FileStream(fullPath, FileMode.Create);
                encoder.Save(fileStream); // 在这里保存
            });

            Log.Information("图像成功保存至 {ImagePath}", fullPath);

            // 保存成功后检查磁盘空间
            await CheckAndCleanupDiskSpaceAsync();

            return fullPath;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存条码 {Barcode} (时间戳 {Timestamp}) 的图像失败", barcode, timestamp);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string?> SaveImageWithWatermarkAsync(BitmapSource? image, PackageInfo packageInfo)
    {
        // 如果没有提供图像，则不保存
        if (image == null)
        {
            return null;
        }

        try
        {
            // 生成带水印的图像
            var watermarkedImage = AddWatermarkToImage(image, packageInfo);
            
            // 使用带水印的图像保存
            return await SaveImageAsync(watermarkedImage, packageInfo.Barcode, packageInfo.CreateTime);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存带水印的图像失败，条码: {Barcode}", packageInfo.Barcode);
            return null;
        }
    }

    /// <summary>
    ///     在图像上添加水印
    /// </summary>
    /// <param name="originalImage">原始图像</param>
    /// <param name="packageInfo">包裹信息</param>
    /// <returns>带水印的图像</returns>
    private static BitmapSource AddWatermarkToImage(BitmapSource originalImage, PackageInfo packageInfo)
    {
        // 确保原始图像已冻结
        if (!originalImage.IsFrozen)
        {
            originalImage.Freeze();
        }

        // 创建DrawingVisual进行绘制
        var visual = new DrawingVisual();
        using (var drawingContext = visual.RenderOpen())
        {
            // 绘制原始图像
            drawingContext.DrawImage(originalImage, new Rect(0, 0, originalImage.Width, originalImage.Height));

            // 准备水印文本
            var watermarkTexts = new List<string>();
            
            // 添加条码
            if (!string.IsNullOrEmpty(packageInfo.Barcode))
            {
                watermarkTexts.Add($"Barcode: {packageInfo.Barcode}");
            }
            
            // 添加重量
            if (packageInfo.Weight > 0)
            {
                watermarkTexts.Add($"Weight: {packageInfo.Weight:F2}kg");
            }
            
            // 添加尺寸
            if (packageInfo.Length.HasValue && packageInfo.Width.HasValue && packageInfo.Height.HasValue)
            {
                watermarkTexts.Add($"Size: {packageInfo.Length:F1}*{packageInfo.Width:F1}*{packageInfo.Height:F1}cm");
            }
            
            // 添加时间
            watermarkTexts.Add($"Time: {packageInfo.CreateTime:yyyy-MM-dd HH:mm:ss}");

            // 设置字体样式
            var typeface = new Typeface(new FontFamily("Microsoft YaHei"), 
                FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var fontSize = Math.Max(12, originalImage.Width / 60); // 根据图像宽度调整字体大小
            var brush = new SolidColorBrush(Colors.Lime); // 绿色字体
            brush.Freeze();

            // 绘制水印文本
            var yOffset = 10.0; // 起始Y偏移
            var lineHeight = fontSize * 1.2; // 行高
            const double xOffset = 10.0; // X偏移

            foreach (var text in watermarkTexts)
            {
                var formattedText = new FormattedText(
                    text,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    fontSize,
                    brush,
                    1.0); // DPI scaling factor

                // 添加黑色背景以提高可读性
                var backgroundBrush = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)); // 半透明黑色
                backgroundBrush.Freeze();
                
                var backgroundRect = new Rect(xOffset - 2, yOffset - 2, 
                    formattedText.Width + 4, formattedText.Height + 4);
                drawingContext.DrawRectangle(backgroundBrush, null, backgroundRect);

                // 绘制文本
                drawingContext.DrawText(formattedText, new Point(xOffset, yOffset));
                yOffset += lineHeight;
            }
        }

        // 渲染到RenderTargetBitmap
        var renderTargetBitmap = new RenderTargetBitmap(
            (int)originalImage.Width,
            (int)originalImage.Height,
            originalImage.DpiX,
            originalImage.DpiY,
            PixelFormats.Pbgra32);

        renderTargetBitmap.Render(visual);
        renderTargetBitmap.Freeze();

        return renderTargetBitmap;
    }

    /// <inheritdoc />
    public async Task CleanupOldImagesAsync(int? retentionDays = null)
    {
        const int defaultRetentionDays = 7; // 默认保留7天
        var effectiveRetentionDays = retentionDays ?? defaultRetentionDays;

        try
        {
            var cameraSettings = _settingsService.LoadSettings<CameraSettings>();

            // 清理普通图片保存路径
            if (!string.IsNullOrEmpty(cameraSettings.ImageSavePath))
            {
                await CleanupExpiredImagesAsync(cameraSettings.ImageSavePath, effectiveRetentionDays);
            }

            // 清理NoreadImage路径
            var appBasePath = AppDomain.CurrentDomain.BaseDirectory;
            var noreadImagePath = Path.Combine(appBasePath, "NoreadImage");
            await CleanupExpiredImagesAsync(noreadImagePath, effectiveRetentionDays);

            Log.Information("手动清理过期图片完成，保留天数: {RetentionDays}", effectiveRetentionDays);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "手动清理过期图片时发生错误，保留天数: {RetentionDays}", effectiveRetentionDays);
            throw;
        }
    }

    private static BitmapEncoder GetEncoder(ImageFormat format)
    {
        return format switch
        {
            ImageFormat.Png => new PngBitmapEncoder(),
            ImageFormat.Bmp => new BmpBitmapEncoder(),
            ImageFormat.Tiff => new TiffBitmapEncoder(),
            _ => new JpegBitmapEncoder() // 默认为 Jpeg
        };
    }

    private static string GetFileExtension(ImageFormat format)
    {
        return format switch
        {
            ImageFormat.Png => ".png",
            ImageFormat.Bmp => ".bmp",
            ImageFormat.Tiff => ".tiff",
            _ => ".jpg" // 默认为 Jpeg
        };
    }

    /// <summary>
    ///     构建NOREAD图像的专门保存路径，保存到软件根目录的NoreadImage文件夹
    /// </summary>
    /// <param name="cameraSettings">相机设置</param>
    /// <param name="timestamp">时间戳</param>
    /// <returns>NOREAD图像的完整保存路径</returns>
    private static string BuildNoreadImagePath(CameraSettings cameraSettings, DateTime timestamp)
    {
        // 获取软件根目录（当前应用程序域的基目录）
        var appBasePath = AppDomain.CurrentDomain.BaseDirectory;

        var year = timestamp.ToString("yyyy");
        var month = timestamp.ToString("MM");
        var day = timestamp.ToString("dd");
        var timeString = timestamp.ToString("HHmmss");

        var directoryPath = Path.Combine(appBasePath, "NoreadImage", year, month, day);
        var fileExtension = GetFileExtension(cameraSettings.ImageFormat);
        var fileName = $"noread{timeString}{fileExtension}";

        return Path.Combine(directoryPath, fileName);
    }

    private static string? BuildImagePath(CameraSettings cameraSettings, string? barcode, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(cameraSettings.ImageSavePath))
        {
            Log.Warning("CameraSettings 中未配置 ImageSavePath.");
            return null;
        }

        var effectiveBarcode =
            string.IsNullOrWhiteSpace(barcode) || barcode.Equals("NOREAD", StringComparison.OrdinalIgnoreCase)
                ? "NOREAD"
                : barcode;
        var isNoRead = effectiveBarcode == "NOREAD";

        var basePath = cameraSettings.ImageSavePath;
        var subDirectory = isNoRead ? "NoRead" : "";
        var year = timestamp.ToString("yyyy");
        var month = timestamp.ToString("MM");
        var day = timestamp.ToString("dd");

        var directoryPath = Path.Combine(basePath, subDirectory, year, month, day);
        var fileExtension = GetFileExtension(cameraSettings.ImageFormat);
        var fileName = $"{effectiveBarcode}_{timestamp:yyyyMMddHHmmssfff}{fileExtension}";

        return Path.Combine(directoryPath, fileName);
    }

    /// <summary>
    ///     检查磁盘空间并在必要时清理最早的图片，同时清理超过30天的图片
    /// </summary>
    private async Task CheckAndCleanupDiskSpaceAsync()
    {
        try
        {
            var cameraSettings = _settingsService.LoadSettings<CameraSettings>();

            // 检查普通图片保存路径
            if (!string.IsNullOrEmpty(cameraSettings.ImageSavePath))
            {
                // 清理超过30天的图片
                await CleanupExpiredImagesAsync(cameraSettings.ImageSavePath, 30);
                // 检查磁盘空间
                await CheckAndCleanupDirectoryAsync(cameraSettings.ImageSavePath);
            }

            // 检查NoreadImage路径
            var appBasePath = AppDomain.CurrentDomain.BaseDirectory;
            var noreadImagePath = Path.Combine(appBasePath, "NoreadImage");
            // 清理超过30天的图片
            await CleanupExpiredImagesAsync(noreadImagePath, 30);
            // 检查磁盘空间
            await CheckAndCleanupDirectoryAsync(noreadImagePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "检查和清理磁盘空间时发生错误");
        }
    }

    /// <summary>
    ///     检查指定目录所在磁盘的空间使用率，如果超过90%则删除最早的图片
    /// </summary>
    /// <param name="directoryPath">要检查的目录路径</param>
    private async Task CheckAndCleanupDirectoryAsync(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        try
        {
            var diskUsagePercentage = GetDiskUsagePercentage(directoryPath);

            if (diskUsagePercentage >= 90.0)
            {
                Log.Warning("磁盘空间使用率已达到 {UsagePercentage:F1}%，开始清理目录: {DirectoryPath}",
                    diskUsagePercentage, directoryPath);

                await DeleteOldestImagesAsync(directoryPath, diskUsagePercentage);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "检查目录 {DirectoryPath} 的磁盘空间时发生错误", directoryPath);
        }
    }

    /// <summary>
    ///     获取指定路径所在磁盘的使用率百分比
    /// </summary>
    /// <param name="path">文件或目录路径</param>
    /// <returns>磁盘使用率百分比</returns>
    private static double GetDiskUsagePercentage(string path)
    {
        var drive = new DriveInfo(Path.GetPathRoot(path) ?? path);
        var totalSize = drive.TotalSize;
        var availableSpace = drive.AvailableFreeSpace;
        var usedSpace = totalSize - availableSpace;

        return (double)usedSpace / totalSize * 100;
    }

    /// <summary>
    ///     删除指定目录下最早的图片文件，直到磁盘使用率降到85%以下
    /// </summary>
    /// <param name="rootPath">根目录路径</param>
    /// <param name="currentUsagePercentage">当前磁盘使用率</param>
    private async Task DeleteOldestImagesAsync(string rootPath, double currentUsagePercentage)
    {
        const double targetUsagePercentage = 85.0; // 目标使用率
        const int maxFilesToDelete = 1000; // 单次最多删除文件数量，防止无限循环

        var deletedCount = 0;
        var currentUsage = currentUsagePercentage;

        await Task.Run(() =>
        {
            while (currentUsage >= targetUsagePercentage && deletedCount < maxFilesToDelete)
            {
                var oldestFiles = GetOldestImageFiles(rootPath, 50); // 每次获取50个最早的文件

                if (oldestFiles.Count == 0)
                {
                    Log.Warning("没有找到可删除的图片文件，停止清理");
                    break;
                }

                foreach (var file in oldestFiles)
                {
                    try
                    {
                        File.Delete(file.FullName);
                        deletedCount++;

                        Log.Information("已删除旧图片文件: {FilePath}", file.FullName);

                        // 每删除10个文件检查一次磁盘使用率
                        if (deletedCount % 10 == 0)
                        {
                            currentUsage = GetDiskUsagePercentage(rootPath);
                            if (currentUsage < targetUsagePercentage)
                            {
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "删除图片文件 {FilePath} 时发生错误", file.FullName);
                    }
                }

                // 更新当前使用率
                currentUsage = GetDiskUsagePercentage(rootPath);
            }
        });

        Log.Information("磁盘空间清理完成，共删除 {DeletedCount} 个文件，当前磁盘使用率: {UsagePercentage:F1}%",
            deletedCount, GetDiskUsagePercentage(rootPath));
    }

    /// <summary>
    ///     获取指定目录下最早的图片文件列表
    /// </summary>
    /// <param name="rootPath">根目录路径</param>
    /// <param name="count">获取文件的数量</param>
    /// <returns>按创建时间排序的最早文件列表</returns>
    private static List<FileInfo> GetOldestImageFiles(string rootPath, int count)
    {
        var imageExtensions = new[]
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".tiff"
        };

        try
        {
            return Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories)
                .Where(file => imageExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                .Select(file => new FileInfo(file))
                .OrderBy(f => f.CreationTime)
                .Take(count)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "获取目录 {RootPath} 下的图片文件时发生错误", rootPath);
            return new List<FileInfo>();
        }
    }

    /// <summary>
    ///     清理指定目录下超过指定天数的图片文件
    /// </summary>
    /// <param name="directoryPath">要清理的目录路径</param>
    /// <param name="retentionDays">保留天数</param>
    private async Task CleanupExpiredImagesAsync(string directoryPath, int retentionDays)
    {
        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        try
        {
            var cutoffDate = DateTime.Now.AddDays(-retentionDays); // 指定天数前的日期
            var deletedCount = 0;

            await Task.Run(() =>
            {
                var expiredFiles = GetExpiredImageFiles(directoryPath, cutoffDate);

                foreach (var file in expiredFiles)
                {
                    try
                    {
                        File.Delete(file.FullName);
                        deletedCount++;

                        Log.Information("已删除过期图片文件 (创建于 {CreationTime}): {FilePath}",
                            file.CreationTime.ToString("yyyy-MM-dd HH:mm:ss"), file.FullName);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "删除过期图片文件 {FilePath} 时发生错误", file.FullName);
                    }
                }

                // 清理空的日期文件夹
                CleanupEmptyDirectories(directoryPath);
            });

            if (deletedCount > 0)
            {
                Log.Information("过期图片清理完成，共删除 {DeletedCount} 个超过{RetentionDays}天的文件，目录: {DirectoryPath}",
                    deletedCount, retentionDays, directoryPath);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "清理目录 {DirectoryPath} 下的过期图片时发生错误，保留天数: {RetentionDays}", directoryPath, retentionDays);
        }
    }

    /// <summary>
    ///     获取指定目录下超过指定日期的图片文件列表
    /// </summary>
    /// <param name="rootPath">根目录路径</param>
    /// <param name="cutoffDate">截止日期</param>
    /// <returns>超过截止日期的图片文件列表</returns>
    private static List<FileInfo> GetExpiredImageFiles(string rootPath, DateTime cutoffDate)
    {
        var imageExtensions = new[]
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".tiff"
        };

        try
        {
            return Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories)
                .Where(file => imageExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                .Select(file => new FileInfo(file))
                .Where(f => f.CreationTime < cutoffDate)
                .OrderBy(f => f.CreationTime)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "获取目录 {RootPath} 下过期图片文件时发生错误", rootPath);
            return new List<FileInfo>();
        }
    }

    /// <summary>
    ///     递归清理空的日期文件夹
    /// </summary>
    /// <param name="rootPath">根目录路径</param>
    private static void CleanupEmptyDirectories(string rootPath)
    {
        try
        {
            var directories = Directory.GetDirectories(rootPath, "*", SearchOption.AllDirectories)
                .OrderByDescending(d => d.Length) // 从最深层的目录开始清理
                .ToList();

            foreach (var directory in directories)
            {
                try
                {
                    if (IsDirectoryEmpty(directory))
                    {
                        Directory.Delete(directory);
                        Log.Information("已删除空目录: {DirectoryPath}", directory);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "删除空目录 {DirectoryPath} 时发生错误", directory);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "清理空目录时发生错误，根路径: {RootPath}", rootPath);
        }
    }

    /// <summary>
    ///     判断目录是否为空（不包含任何文件和子目录）
    /// </summary>
    /// <param name="directoryPath">目录路径</param>
    /// <returns>如果目录为空则返回true</returns>
    private static bool IsDirectoryEmpty(string directoryPath)
    {
        try
        {
            return !Directory.EnumerateFileSystemEntries(directoryPath).Any();
        }
        catch
        {
            return false;
        }
    }
}