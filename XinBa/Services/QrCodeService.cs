using System.IO;
using System.Windows.Media.Imaging;
using QRCoder;
using Serilog;

namespace XinBa.Services;

/// <summary>
///     使用 QRCoder 库实现二维码生成服务
/// </summary>
public class QrCodeService : IQrCodeService
{
    /// <inheritdoc />
    public BitmapSource? Generate(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            Log.Warning("QR code generation skipped: input text is empty.");
            return null;
        }

        try
        {
            using var qrGenerator = new QRCodeGenerator();
            using var qrCodeData = qrGenerator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
            using var qrCode = new PngByteQRCode(qrCodeData);
            var qrCodeImage = qrCode.GetGraphic(20);

            using var ms = new MemoryStream(qrCodeImage);
            var bitmapImage = new BitmapImage();
            bitmapImage.BeginInit();
            bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
            bitmapImage.StreamSource = ms;
            bitmapImage.EndInit();
            bitmapImage.Freeze(); // Freeze for cross-thread accessibility
            return bitmapImage;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to generate QR code for text: {Text}", text);
            return null;
        }
    }
}