using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Common.Models.Package;
using Serilog;

namespace CangFenBao.SDK
{
    /// <summary>
    /// SDK图像处理服务，负责处理和保存包裹图像，支持添加水印。
    /// </summary>
    internal class SdkImageService(SdkConfig sdkConfig)
    {
        /// <summary>
        /// 异步处理并保存包裹图像。
        /// </summary>
        /// <param name="package">包含图像和相关信息的包裹对象</param>
        public async Task ProcessAndSaveImageAsync(PackageInfo package)
        {
            if (package.Image == null)
            {
                Log.Debug("包裹 {Barcode} 没有图像数据，跳过图像处理", package.Barcode);
                return;
            }

            if (!sdkConfig.SaveImages)
            {
                Log.Debug("图像保存功能已禁用，跳过包裹 {Barcode} 的图像保存", package.Barcode);
                return;
            }

            if (string.IsNullOrEmpty(sdkConfig.ImageSavePath))
            {
                Log.Warning("图像保存路径未配置，无法保存包裹 {Barcode} 的图像", package.Barcode);
                return;
            }

            Log.Information("开始处理包裹 {Barcode} 的图像，尺寸: {Width}x{Height}", 
                package.Barcode, package.Image.PixelWidth, package.Image.PixelHeight);

            try
            {
                await Task.Run(() =>
                {
                    var finalImage = package.Image;

                    // 如果启用水印功能，则添加水印
                    if (sdkConfig.AddWatermark)
                    {
                        Log.Debug("开始为包裹 {Barcode} 添加水印", package.Barcode);
                        try
                        {
                            finalImage = AddWatermark(package.Image, package);
                            Log.Debug("包裹 {Barcode} 水印添加成功", package.Barcode);
                        }
                        catch (Exception watermarkEx)
                        {
                            Log.Error(watermarkEx, "为包裹 {Barcode} 添加水印时发生异常，将使用原始图像", package.Barcode);
                            finalImage = package.Image; // 回退到原始图像
                        }
                    }
                    else
                    {
                        Log.Debug("水印功能已禁用，包裹 {Barcode} 使用原始图像", package.Barcode);
                    }

                    // 保存图像
                    SaveImage(finalImage, package);
                });

                Log.Information("包裹 {Barcode} 图像处理完成", package.Barcode);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理包裹 {Barcode} 图像时发生异常", package.Barcode);
                throw;
            }
        }

        /// <summary>
        /// 保存图像到指定路径。
        /// </summary>
        /// <param name="image">要保存的图像</param>
        /// <param name="package">包裹信息，用于生成文件名</param>
        private void SaveImage(BitmapSource image, PackageInfo package)
        {
            try
            {
                // 确保保存目录存在
                var directoryPath = Path.GetDirectoryName(sdkConfig.ImageSavePath);
                if (!string.IsNullOrEmpty(directoryPath) && !Directory.Exists(directoryPath))
                {
                    Log.Debug("创建图像保存目录: {DirectoryPath}", directoryPath);
                    Directory.CreateDirectory(directoryPath);
                    Log.Information("图像保存目录创建成功: {DirectoryPath}", directoryPath);
                }

                // 生成文件名：条码_时间戳.jpg
                var fileName = $"{package.Barcode}_{DateTime.Now:yyyyMMddHHmmss}.jpg";
                var filePath = Path.Combine(directoryPath ?? "", fileName);

                Log.Debug("开始保存图像到文件: {FilePath}", filePath);

                // 创建JPEG编码器
                var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
                encoder.Frames.Add(BitmapFrame.Create(image));

                // 保存到文件
                using var fileStream = new FileStream(filePath, FileMode.Create);
                encoder.Save(fileStream);

                var fileInfo = new FileInfo(filePath);
                Log.Information("包裹 {Barcode} 图像保存成功，文件路径: {FilePath}, 文件大小: {FileSize} KB", 
                    package.Barcode, filePath, fileInfo.Length / 1024.0);
            }
            catch (DirectoryNotFoundException dirEx)
            {
                Log.Error(dirEx, "保存包裹 {Barcode} 图像时目录未找到，配置路径: {ImageSavePath}", 
                    package.Barcode, sdkConfig.ImageSavePath);
                throw;
            }
            catch (UnauthorizedAccessException authEx)
            {
                Log.Error(authEx, "保存包裹 {Barcode} 图像时访问被拒绝，检查目录权限: {ImageSavePath}", 
                    package.Barcode, sdkConfig.ImageSavePath);
                throw;
            }
            catch (IOException ioEx)
            {
                Log.Error(ioEx, "保存包裹 {Barcode} 图像时发生I/O异常，路径: {ImageSavePath}", 
                    package.Barcode, sdkConfig.ImageSavePath);
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存包裹 {Barcode} 图像时发生未预期异常", package.Barcode);
                throw;
            }
        }

        /// <summary>
        /// 为图像添加水印。
        /// </summary>
        /// <param name="originalImage">原始图像</param>
        /// <param name="package">包裹信息，用于生成水印文本</param>
        /// <returns>添加了水印的图像</returns>
        private RenderTargetBitmap AddWatermark(BitmapSource originalImage, PackageInfo package)
        {
            try
            {
                Log.Debug("开始为包裹 {Barcode} 生成水印图像", package.Barcode);

                var visual = new DrawingVisual();
                using var drawingContext = visual.RenderOpen();

                // 绘制原始图像
                var backgroundRect = new Rect(0, 0, originalImage.PixelWidth, originalImage.PixelHeight);
                drawingContext.DrawImage(originalImage, backgroundRect);

                // 生成水印文本
                var watermarkText = sdkConfig.WatermarkFormat
                    .Replace("{barcode}", package.Barcode)
                    .Replace("{dateTime}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                Log.Debug("包裹 {Barcode} 水印文本: {WatermarkText}", package.Barcode, watermarkText);

                // 创建水印文本
                var typeface = new Typeface("Arial");
                var fontSize = Math.Max(12, originalImage.PixelWidth / 50); // 根据图像宽度动态调整字体大小
                var backgroundBrush = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)); // 半透明黑色背景
                
                var formattedText = new FormattedText(
                    watermarkText,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    fontSize,
                    Brushes.White,
                    1.0);

                // 计算水印位置（右下角）
                var textX = originalImage.PixelWidth - formattedText.Width - 10;
                var textY = originalImage.PixelHeight - formattedText.Height - 10;

                // 绘制半透明背景
                var backgroundRect2 = new Rect(textX - 5, textY - 2, formattedText.Width + 10, formattedText.Height + 4);
                drawingContext.DrawRectangle(backgroundBrush, null, backgroundRect2);

                // 绘制水印文本
                drawingContext.DrawText(formattedText, new Point(textX, textY));

                Log.Debug("包裹 {Barcode} 水印绘制完成，位置: ({X}, {Y}), 字体大小: {FontSize}", 
                    package.Barcode, textX, textY, fontSize);

                // 创建渲染目标
                var renderTargetBitmap = new RenderTargetBitmap(
                    originalImage.PixelWidth,
                    originalImage.PixelHeight,
                    96, 96,
                    PixelFormats.Pbgra32);

                renderTargetBitmap.Render(visual);
                renderTargetBitmap.Freeze(); // 使图像不可变，提高性能

                Log.Debug("包裹 {Barcode} 水印图像渲染完成", package.Barcode);
                return renderTargetBitmap;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "为包裹 {Barcode} 添加水印时发生异常，原始图像尺寸: {Width}x{Height}", 
                    package.Barcode, originalImage.PixelWidth, originalImage.PixelHeight);
                throw;
            }
        }
    }
} 