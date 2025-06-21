using System.Windows.Media.Imaging;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.Camera.Models.Camera;
using DeviceService.DataSourceDevices.Camera.Models.Camera.Enums;
using Serilog;

namespace DeviceService.DataSourceDevices.Services;

/// <summary>
/// 负责根据配置将图像保存到本地磁盘的服务。
/// </summary>
public class ImageSavingService(ISettingsService settingsService) : IImageSavingService
{
    private readonly ISettingsService _settingsService =
        settingsService ?? throw new ArgumentNullException(nameof(settingsService));

    // 清理计数器，用于控制清理频率
    private static int _cleanupCounter = 0;
    private static readonly object _cleanupLock = new object();
    
    // 图片保留天数（一周）
    private const int RetentionDays = 7;
    
    // 每保存多少张图片后执行一次清理（避免频繁清理）
    private const int CleanupInterval = 100;

    /// <inheritdoc />
    public string? GenerateImagePath(string? barcode, DateTime timestamp)
    {
        var cameraSettings = _settingsService.LoadSettings<CameraSettings>();
        return !cameraSettings.EnableImageSaving
            ? null
            : // 保存已禁用
            BuildImagePath(cameraSettings, barcode, timestamp);
    }

    /// <inheritdoc />
    public async Task<string?> SaveImageAsync(BitmapSource? image, string? barcode, DateTime timestamp)
    {
        var cameraSettings = _settingsService.LoadSettings<CameraSettings>();

        if (!cameraSettings.EnableImageSaving || image == null)
        {
            return null; // 保存已禁用或未提供图像
        }

        try
        {
            var fullPath = BuildImagePath(cameraSettings, barcode, timestamp);

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
            
            // 定期执行清理任务
            TriggerPeriodicCleanup(cameraSettings);
            
            return fullPath;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存条码 {Barcode} (时间戳 {Timestamp}) 的图像失败", barcode, timestamp);
            return null;
        }
    }

    /// <summary>
    /// 定期触发清理任务
    /// </summary>
    private static void TriggerPeriodicCleanup(CameraSettings cameraSettings)
    {
        lock (_cleanupLock)
        {
            _cleanupCounter++;
            if (_cleanupCounter >= CleanupInterval)
            {
                _cleanupCounter = 0;
                
                // 在后台线程执行清理，避免阻塞图片保存
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await CleanupOldImagesInternalAsync(cameraSettings);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "定期清理过期图片时发生错误");
                    }
                });
            }
        }
    }

    /// <summary>
    /// 清理超过指定天数的图片文件（内部静态方法）
    /// </summary>
    /// <param name="cameraSettings">相机设置</param>
    /// <param name="retentionDays">保留天数，默认7天</param>
    private static async Task CleanupOldImagesInternalAsync(CameraSettings cameraSettings, int retentionDays = RetentionDays)
    {
        if (string.IsNullOrEmpty(cameraSettings.ImageSavePath) || !Directory.Exists(cameraSettings.ImageSavePath))
        {
            Log.Debug("图片保存路径未配置或不存在，跳过清理");
            return;
        }

        var cutoffDate = DateTime.Now.Date.AddDays(-retentionDays);
        Log.Information("开始清理 {CutoffDate:yyyy-MM-dd} 之前的图片文件，保留天数: {RetentionDays} 天", cutoffDate, retentionDays);

        try
        {
            var basePath = cameraSettings.ImageSavePath;
            var deletedFilesCount = 0;
            var deletedFoldersCount = 0;
            var totalSize = 0L;

            // 扫描主目录和 NoRead 子目录
            var scanPaths = new[]
            {
                basePath,
                Path.Combine(basePath, "NoRead")
            };

            foreach (var scanPath in scanPaths)
            {
                if (!Directory.Exists(scanPath)) continue;

                var result = await CleanupDirectoryAsync(scanPath, cutoffDate);
                deletedFilesCount += result.FilesDeleted;
                deletedFoldersCount += result.FoldersDeleted;
                totalSize += result.TotalSize;
            }

            if (deletedFilesCount > 0)
            {
                Log.Information("图片清理完成: 删除了 {FilesCount} 个文件，{FoldersCount} 个文件夹，释放空间 {Size:F2} MB",
                    deletedFilesCount, deletedFoldersCount, totalSize / 1024.0 / 1024.0);
            }
            else
            {
                Log.Debug("图片清理完成: 没有找到需要删除的过期文件");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "清理过期图片时发生错误");
        }
    }

    /// <inheritdoc />
    public async Task CleanupOldImagesAsync(int? retentionDays = null)
    {
        var cameraSettings = _settingsService.LoadSettings<CameraSettings>();
        var actualRetentionDays = retentionDays ?? RetentionDays;
        
        Log.Information("手动触发图片清理，保留天数: {RetentionDays}", actualRetentionDays);
        await CleanupOldImagesInternalAsync(cameraSettings, actualRetentionDays);
    }

    /// <summary>
    /// 递归清理指定目录中的过期文件和空文件夹
    /// </summary>
    private static async Task<CleanupResult> CleanupDirectoryAsync(string directoryPath, DateTime cutoffDate)
    {
        var result = new CleanupResult();

        if (!Directory.Exists(directoryPath))
            return result;

        try
        {
            var directory = new DirectoryInfo(directoryPath);
            
            // 处理子目录
            var subDirectories = directory.GetDirectories();
            foreach (var subDir in subDirectories)
            {
                var subResult = await CleanupDirectoryAsync(subDir.FullName, cutoffDate);
                result.FilesDeleted += subResult.FilesDeleted;
                result.FoldersDeleted += subResult.FoldersDeleted;
                result.TotalSize += subResult.TotalSize;

                // 如果子目录为空，删除它
                if (IsDirectoryEmpty(subDir.FullName))
                {
                    try
                    {
                        subDir.Delete();
                        result.FoldersDeleted++;
                        Log.Debug("删除空目录: {DirectoryPath}", subDir.FullName);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "删除空目录失败: {DirectoryPath}", subDir.FullName);
                    }
                }
            }

            // 处理当前目录中的文件
            var files = directory.GetFiles("*.*", SearchOption.TopDirectoryOnly);
            foreach (var file in files)
            {
                try
                {
                    // 检查文件是否是图片文件
                    if (!IsImageFile(file.Extension))
                        continue;

                    // 检查文件是否过期（基于文件创建时间和修改时间的较早者）
                    var fileDate = file.CreationTime < file.LastWriteTime ? file.CreationTime : file.LastWriteTime;
                    
                    if (fileDate.Date < cutoffDate)
                    {
                        var fileSize = file.Length;
                        file.Delete();
                        result.FilesDeleted++;
                        result.TotalSize += fileSize;
                        
                        Log.Debug("删除过期图片: {FilePath} (文件日期: {FileDate:yyyy-MM-dd})", 
                            file.FullName, fileDate);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "删除文件失败: {FilePath}", file.FullName);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理目录时发生错误: {DirectoryPath}", directoryPath);
        }

        return result;
    }

    /// <summary>
    /// 检查目录是否为空（不包含任何文件或子目录）
    /// </summary>
    private static bool IsDirectoryEmpty(string directoryPath)
    {
        try
        {
            return !Directory.EnumerateFileSystemEntries(directoryPath).Any();
        }
        catch
        {
            return false; // 出错时认为目录不为空，避免误删
        }
    }

    /// <summary>
    /// 检查文件是否是图片文件
    /// </summary>
    private static bool IsImageFile(string extension)
    {
        var imageExtensions = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".tiff", ".tif" };
        return imageExtensions.Contains(extension.ToLowerInvariant());
    }

    /// <summary>
    /// 清理结果统计
    /// </summary>
    private class CleanupResult
    {
        public int FilesDeleted { get; set; }
        public int FoldersDeleted { get; set; }
        public long TotalSize { get; set; }
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
}