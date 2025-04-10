using System.IO;
using System.Windows.Media.Imaging;
using Serilog;
using System.Windows; // 添加对 System.Windows 的引用

namespace XinBeiYang.Services;

/// <summary>
/// 实现 IImageStorageService 接口，将图像保存到本地文件系统。
/// </summary>
public class LocalImageStorageService : IImageStorageService
{
    private readonly string _baseStoragePath;

    public LocalImageStorageService()
    {
        // 默认存储路径：应用程序基础目录\Images
        _baseStoragePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Images");
        Log.Information("本地图像存储路径设置为: {Path}", _baseStoragePath);
    }

    /// <summary>
    /// 使用指定的基础路径初始化 LocalImageStorageService 类的新实例。
    /// </summary>
    /// <param name="baseStoragePath">存储图像的根目录。</param>
    public LocalImageStorageService(string baseStoragePath)
    {
        _baseStoragePath = baseStoragePath;
        Log.Information("本地图像存储路径设置为: {Path}", _baseStoragePath);
    }

    /// <inheritdoc />
    public async Task<string?> SaveImageAsync(BitmapSource? image, string barcode, DateTime createTime)
    {
        if (image == null)
        {
            Log.Warning("尝试保存空图像，条码为 {Barcode}", barcode);
            return null;
        }

        try
        {
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
            var timestamp = createTime.ToString("yyyyMMddHHmmssfff");
            var fileName = $"{sanitizedBarcode}_{timestamp}.jpg";
            var filePath = Path.Combine(dailyFolderPath, fileName);

            // 使用 JpegBitmapEncoder 保存 BitmapSource
            // var encoder = new JpegBitmapEncoder(); // 移动到 Task.Run 内部
            // 确保传入的 image 是线程安全的（例如，冻结的克隆）
            // encoder.Frames.Add(BitmapFrame.Create(image)); // 移动到 Task.Run 内部

            // 异步创建文件流
            await using (var fileStream = new FileStream(filePath, FileMode.Create))
            {
                // 将 Encoder 创建、Frame 添加和 Save 操作都放到后台线程执行
                await Task.Run(() => 
                {
                     try
                     {
                         var encoder = new JpegBitmapEncoder(); // 在后台线程创建 Encoder
                         // 确保传入的 image 是线程安全的
                         encoder.Frames.Add(BitmapFrame.Create(image)); // 在后台线程添加 Frame
                         encoder.Save(fileStream); // 在后台线程保存
                     }
                     catch (Exception ex)
                     {
                         // 如果 Task.Run 内部发生错误，需要一种方式来传播或记录它
                         // 这里直接记录错误，因为 Task.Run 外部的 catch 块可能无法捕获它
                         Log.Error(ex, "在后台线程 Task.Run 中 encoder.Save 失败，条码 {Barcode}", barcode);
                         // 抛出异常可能导致应用程序崩溃，取决于外部如何处理Task
                         // 更好的方法可能是设置一个标志或返回特定值指示失败
                         throw; // 重新抛出，让外部 catch 捕获（如果可能）
                     }
                });
                // 移除 Dispatcher 调用
                /*
                bool saveSuccess = false;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        // var encoder = new JpegBitmapEncoder(); // 在 UI 线程创建 Encoder
                        // encoder.Frames.Add(BitmapFrame.Create(image)); // 在 UI 线程添加 Frame
                        encoder.Save(fileStream); // 在 UI 线程保存
                        saveSuccess = true;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "BitmapEncoder 操作（创建/添加帧/保存）在 UI 线程上执行时失败，条码 {Barcode}", barcode);
                        // saveSuccess 保持 false
                    }
                });

                // 如果保存失败，记录日志并返回 null
                if (!saveSuccess)
                {
                    Log.Warning("图像保存操作未成功完成（可能在UI线程上失败），条码 {Barcode}", barcode);
                    // 尝试关闭并删除可能不完整的文件
                    try { await fileStream.FlushAsync(); fileStream.Close(); File.Delete(filePath); }
                    catch(Exception cleanupEx) { Log.Warning(cleanupEx, "清理失败的图像文件时出错: {FilePath}", filePath); }
                    return null;
                }
                */
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

    /// <summary>
    /// 移除或替换文件名中的非法字符。
    /// </summary>
    private static string SanitizeFileName(string fileName)
    {
        // 移除Windows文件名中的非法字符
        // 如有需要可以添加更具体的清理规则
        return Path.GetInvalidFileNameChars().Aggregate(fileName, (current, c) => current.Replace(c.ToString(), "_"));
    }
} 