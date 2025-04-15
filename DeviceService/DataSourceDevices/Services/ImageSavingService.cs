using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.Camera.Models.Camera;
using DeviceService.DataSourceDevices.Camera.Models.Camera.Enums;
using Serilog;

namespace DeviceService.DataSourceDevices.Services;

/// <summary>
/// Service responsible for saving images to the local disk based on configuration.
/// </summary>
public class ImageSavingService(ISettingsService settingsService) : IImageSavingService
{
    private readonly ISettingsService _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));

    /// <inheritdoc />
    public string? GenerateImagePath(string? barcode, DateTime timestamp)
    {
        var cameraSettings = _settingsService.LoadSettings<CameraSettings>();
        if (cameraSettings == null || !cameraSettings.EnableImageSaving)
        {
            return null; // 保存已禁用
        }

        return BuildImagePath(cameraSettings, barcode, timestamp);
    }

    /// <inheritdoc />
    public async Task<string?> SaveImageAsync(BitmapSource? image, string? barcode, DateTime timestamp)
    {
        var cameraSettings = _settingsService.LoadSettings<CameraSettings>();

        if (cameraSettings == null || !cameraSettings.EnableImageSaving || image == null)
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
                BitmapEncoder encoder = GetEncoder(imageFormat); // 在这里创建编码器
                BitmapFrame frame = BitmapFrame.Create(image); // 在这里创建帧 (使用已冻结的图像)
                encoder.Frames.Add(frame);

                using var fileStream = new FileStream(fullPath, FileMode.Create);
                encoder.Save(fileStream); // 在这里保存
            });

            Log.Information("图像成功保存至 {ImagePath}", fullPath);
            return fullPath;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存条码 {Barcode} (时间戳 {Timestamp}) 的图像失败", barcode, timestamp);
            return null;
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

    private string GetFileExtension(ImageFormat format)
    {
        return format switch
        {
            ImageFormat.Png => ".png",
            ImageFormat.Bmp => ".bmp",
            ImageFormat.Tiff => ".tiff",
            _ => ".jpg" // 默认为 Jpeg
        };
    }

    private string? BuildImagePath(CameraSettings cameraSettings, string? barcode, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(cameraSettings.ImageSavePath))
        {
            Log.Warning("CameraSettings 中未配置 ImageSavePath.");
            return null;
        }

        var effectiveBarcode = string.IsNullOrWhiteSpace(barcode) || barcode.Equals("NOREAD", StringComparison.OrdinalIgnoreCase) ? "NOREAD" : barcode;
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