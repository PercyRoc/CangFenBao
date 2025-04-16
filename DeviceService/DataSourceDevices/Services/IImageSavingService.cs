using System.Windows.Media.Imaging;

namespace DeviceService.DataSourceDevices.Services;

/// <summary>
/// Interface for the image saving service.
/// </summary>
public interface IImageSavingService
{
    /// <summary>
    /// Saves the provided image asynchronously to the configured location.
    /// </summary>
    /// <param name="image">The image to save.</param>
    /// <param name="barcode">The barcode associated with the image (can be null or "NOREAD").</param>
    /// <param name="timestamp">The timestamp for the image.</param>
    /// <returns>The full path where the image was saved, or null if saving failed or was disabled.</returns>
    Task<string?> SaveImageAsync(BitmapSource? image, string? barcode, DateTime timestamp);

    /// <summary>
    /// Generates the potential full path for saving an image based on configuration, barcode, and timestamp,
    /// without actually saving the file or creating directories.
    /// </summary>
    /// <param name="barcode">The barcode associated with the image (can be null or "NOREAD").</param>
    /// <param name="timestamp">The timestamp for the image.</param>
    /// <returns>The potential full path where the image would be saved, or null if saving is disabled or path cannot be determined.</returns>
    string? GenerateImagePath(string? barcode, DateTime timestamp);
} 