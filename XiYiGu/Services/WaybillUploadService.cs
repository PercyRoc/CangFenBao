using System.IO;
using System.Net.Http;
using Common.Models.Package;
using Common.Services.Settings;
using Presentation_XiYiGu.Models;
using Presentation_XiYiGu.Models.Settings;
using Serilog;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace Presentation_XiYiGu.Services;

/// <summary>
///     运单上传服务
/// </summary>
public class WaybillUploadService
{
    private readonly ApiService _apiService;
    private readonly object _queueLock = new();
    private readonly ISettingsService _settingsService;
    private readonly Queue<PackageInfo> _uploadQueue = new();
    private ApiSettings _apiSettings = new();
    private bool _isUploading;

    /// <summary>
    ///     构造函数
    /// </summary>
    /// <param name="httpClientFactory">HTTP客户端工厂</param>
    /// <param name="settingsService">设置服务</param>
    public WaybillUploadService(IHttpClientFactory httpClientFactory, ISettingsService settingsService)
    {
        _settingsService = settingsService;

        // 加载API设置
        LoadApiSettings();

        // 创建API服务
        var httpClient = httpClientFactory.CreateClient();
        _apiService = new ApiService(
            httpClient,
            _apiSettings.BaseUrl,
            _apiSettings.AesKey);
    }

    /// <summary>
    ///     加载API设置
    /// </summary>
    private void LoadApiSettings()
    {
        try
        {
            _apiSettings = _settingsService.LoadSettings<ApiSettings>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载API设置时发生错误");
        }
    }

    /// <summary>
    ///     添加包裹到上传队列
    /// </summary>
    /// <param name="package">包裹信息</param>
    public void EnqueuePackage(PackageInfo package)
    {
        if (!_apiSettings.Enabled) return;

        lock (_queueLock)
        {
            _uploadQueue.Enqueue(package);
        }

        // 如果没有正在上传，则开始上传
        if (!_isUploading) _ = ProcessUploadQueueAsync();
    }

    /// <summary>
    ///     处理上传队列
    /// </summary>
    private async Task ProcessUploadQueueAsync()
    {
        if (_isUploading) return;

        _isUploading = true;

        try
        {
            while (true)
            {
                PackageInfo? package;
                lock (_queueLock)
                {
                    if (_uploadQueue.Count == 0) break;

                    package = _uploadQueue.Dequeue();
                }

                await UploadPackageAsync(package);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理上传队列时发生错误");
        }
        finally
        {
            _isUploading = false;
        }
    }

    /// <summary>
    ///     上传包裹
    /// </summary>
    /// <param name="package">包裹信息</param>
    private async Task UploadPackageAsync(PackageInfo package)
    {
        try
        {
            // 转换为运单记录
            var waybill = ConvertToWaybillRecord(package);

            // 上传运单记录
            var response = await _apiService.UploadWaybillAsync(_apiSettings.MachineMx, [waybill]);

            if (response.IsSuccess)
            {
                Log.Information("运单上传成功，运单号: {WaybillNumber}", package.Barcode);

                // 如果有图片对象，则上传图片
                if (package.Image != null) await UploadPackageImageAsync(package);
            }
            else
            {
                Log.Warning("运单上传失败，运单号: {WaybillNumber}, 错误: {Error}", package.Barcode, response.Msg);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "上传包裹时发生错误，运单号: {WaybillNumber}", package.Barcode);
        }
    }

    /// <summary>
    ///     上传包裹图片
    /// </summary>
    /// <param name="package">包裹信息</param>
    private async Task UploadPackageImageAsync(PackageInfo package)
    {
        try
        {
            string tempImagePath;

            // 将图片对象保存到临时文件
            try
            {
                // 创建临时文件夹（如果不存在）
                var tempDir = Path.Combine(Path.GetTempPath(), "XiYiGu", "TempImages");
                Directory.CreateDirectory(tempDir);

                // 创建临时文件路径
                tempImagePath = Path.Combine(tempDir, $"{package.Barcode}_{DateTime.Now:yyyyMMddHHmmss}.jpg");

                // 保存图片
                if (package.Image != null) await package.Image.SaveAsync(tempImagePath, new JpegEncoder());
                Log.Information("已将包裹图片保存到临时文件: {FilePath}", tempImagePath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存包裹图片到临时文件时发生错误");
                return;
            }

            try
            {
                // 上传图片
                var weightTime = package.CreateTime.ToString("yyyy-MM-dd HH:mm:ss");
                var response = await _apiService.UploadWaybillImageAsync(
                    _apiSettings.MachineMx,
                    package.Barcode,
                    weightTime,
                    tempImagePath);

                if (response.IsSuccess)
                    Log.Information("运单图片上传成功，运单号: {WaybillNumber}", package.Barcode);
                else
                    Log.Warning("运单图片上传失败，运单号: {WaybillNumber}, 错误: {Error}", package.Barcode, response.Msg);
            }
            finally
            {
                // 删除临时文件
                if (!string.IsNullOrEmpty(tempImagePath) && File.Exists(tempImagePath))
                    try
                    {
                        File.Delete(tempImagePath);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "删除临时图片文件时发生错误: {FilePath}", tempImagePath);
                    }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "上传包裹图片时发生错误，运单号: {WaybillNumber}", package.Barcode);
        }
    }

    /// <summary>
    ///     将包裹信息转换为运单记录
    /// </summary>
    /// <param name="package">包裹信息</param>
    /// <returns>运单记录</returns>
    private static WaybillRecord ConvertToWaybillRecord(PackageInfo package)
    {
        // 计算尺寸字符串
        var sizeStr = string.Empty;
        if (package is { Length: not null, Width: not null, Height: not null })
            // 修改为长*宽*高格式，单位转换为毫米（原厘米值乘以10）
            sizeStr = $"{package.Length.Value * 10:F0}*{package.Width.Value * 10:F0}*{package.Height.Value * 10:F0}";

        // 计算体积字符串
        var volumeStr = string.Empty;
        if (package.Volume.HasValue) volumeStr = $"{package.Volume.Value:F0}";

        return new WaybillRecord
        {
            WaybillNumber = package.Barcode,
            Weight = package.Weight / 1000, // 转换为千克（1克 = 0.001千克）
            WeightTime = package.CreateTime.ToString("yyyy-MM-dd HH:mm:ss"),
            JtWaybillSize = sizeStr,
            JtWaybillVolume = volumeStr,
            JtHistoryWeight = package.Weight / 1000 // 历史重量，与当前重量相同
        };
    }

    /// <summary>
    ///     生成测试数据
    /// </summary>
    /// <param name="count">生成数量</param>
    /// <returns>测试数据列表</returns>
    private static
        List<(string Barcode, float Weight, double Length, double Width, double Height, Image<Rgba32>? Image)>
        GenerateTestData(int count)
    {
        var random = new Random();
        var testData =
            new List<(string Barcode, float Weight, double Length, double Width, double Height, Image<Rgba32>? Image
                )>();
        var usedBarcodes = new HashSet<string>();

        for (var i = 0; i < count; i++)
        {
            // 生成不重复的运单号
            string barcode;
            do
            {
                barcode = $"TEST{DateTime.Now:yyyyMMdd}{random.Next(1000, 9999)}";
            } while (usedBarcodes.Contains(barcode));

            usedBarcodes.Add(barcode);

            // 生成随机重量（1-10kg）
            var weight = random.Next(1000, 10000);

            // 生成随机尺寸（10-100cm）
            var length = random.Next(10, 100);
            var width = random.Next(10, 100);
            var height = random.Next(10, 100);

            // 创建测试图片
            Image<Rgba32>? image = null;
            try
            {
                // 创建一个800x600的图片
                image = new Image<Rgba32>(800, 600);

                // 填充白色背景
                for (var y = 0; y < image.Height; y++)
                for (var x = 0; x < image.Width; x++)
                    image[x, y] = Color.White;

                // 添加水印文字
                var font = SystemFonts.CreateFont("Arial", 32);
                var text = $"测试运单 {barcode}";
                var textSize = TextMeasurer.Measure(text, new TextOptions(font));

                // 计算文字位置（居中）
                var textX = (800 - textSize.Width) / 2;
                var textY = (600 - textSize.Height) / 2;

                // 绘制文字
                using var textImage = new Image<Rgba32>((int)textSize.Width, (int)textSize.Height);
                for (var y = 0; y < textImage.Height; y++)
                for (var x = 0; x < textImage.Width; x++)
                    textImage[x, y] = Color.Black;

                // 将文字图片复制到主图片
                for (var y = 0; y < textImage.Height; y++)
                for (var x = 0; x < textImage.Width; x++)
                {
                    var targetX = (int)textX + x;
                    var targetY = (int)textY + y;
                    if (targetX >= 0 && targetX < image.Width && targetY >= 0 && targetY < image.Height)
                        image[targetX, targetY] = textImage[x, y];
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "生成测试图片时发生错误");
            }

            testData.Add((barcode, weight, length, width, height, image));
        }

        return testData;
    }

    /// <summary>
    ///     批量测试上传运单信息和图片
    /// </summary>
    /// <param name="count">测试数量</param>
    /// <returns>上传结果列表</returns>
    public async Task<List<(string Barcode, ApiResponse InfoResponse, ApiResponse? ImageResponse)>>
        TestBatchUploadAsync(int count = 50)
    {
        var results = new List<(string Barcode, ApiResponse InfoResponse, ApiResponse? ImageResponse)>();
        var testData = GenerateTestData(count);
        var random = new Random();
        var baseTime = DateTime.Now;

        foreach (var (barcode, weight, length, width, height, image) in testData)
            try
            {
                // 重新加载API设置
                LoadApiSettings();

                if (!_apiSettings.Enabled)
                {
                    results.Add((barcode,
                        new ApiResponse { Code = 400, Msg = "API未启用" },
                        null));
                    continue;
                }

                // 为每个运单生成不同的时间（基准时间前后随机1小时内）
                var randomMinutes = random.Next(-60, 60);
                var createTime = baseTime.AddMinutes(randomMinutes);

                // 创建测试包裹信息
                var package = new PackageInfo
                {
                    Barcode = barcode,
                    Weight = weight,
                    Length = length,
                    Width = width,
                    Height = height,
                    Volume = length * width * height,
                    CreateTime = createTime,
                    Image = image
                };

                // 上传运单信息
                Log.Information("开始测试上传运单信息，运单号: {WaybillNumber}, 时间: {CreateTime}", barcode, createTime);

                // 转换为运单记录
                var waybill = ConvertToWaybillRecord(package);

                // 上传运单记录
                var infoResponse = await _apiService.UploadWaybillAsync(_apiSettings.MachineMx, [waybill]);

                // 如果运单信息上传成功且有图片，则上传图片
                ApiResponse? imageResponse = null;
                if (infoResponse.IsSuccess && image != null)
                {
                    Log.Information("开始测试上传运单图片，运单号: {WaybillNumber}, 时间: {CreateTime}", barcode, createTime);

                    // 保存图片到临时文件
                    string? tempImagePath = null;
                    try
                    {
                        // 创建临时文件夹（如果不存在）
                        var tempDir = Path.Combine(Path.GetTempPath(), "XiYiGu", "TempImages");
                        Directory.CreateDirectory(tempDir);

                        // 创建临时文件路径
                        tempImagePath = Path.Combine(tempDir, $"{barcode}_{DateTime.Now:yyyyMMddHHmmss}.jpg");

                        // 保存图片
                        await image.SaveAsync(tempImagePath, new JpegEncoder());
                        Log.Information("已将测试图片保存到临时文件: {FilePath}", tempImagePath);

                        // 上传图片
                        var weightTime = package.CreateTime.ToString("yyyy-MM-dd HH:mm:ss");
                        imageResponse = await _apiService.UploadWaybillImageAsync(
                            _apiSettings.MachineMx,
                            barcode,
                            weightTime,
                            tempImagePath);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "测试上传运单图片时发生错误");
                        imageResponse = new ApiResponse
                        {
                            Code = 500,
                            Msg = $"上传图片异常: {ex.Message}"
                        };
                    }
                    finally
                    {
                        // 删除临时文件
                        if (!string.IsNullOrEmpty(tempImagePath) && File.Exists(tempImagePath))
                            try
                            {
                                File.Delete(tempImagePath);
                            }
                            catch (Exception ex)
                            {
                                Log.Warning(ex, "删除临时测试图片文件时发生错误: {FilePath}", tempImagePath);
                            }
                    }
                }

                results.Add((barcode, infoResponse, imageResponse));

                // 释放图片资源
                image?.Dispose();

                // 添加短暂延迟，避免请求过于频繁
                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "测试上传运单时发生错误，运单号: {Barcode}", barcode);
                results.Add((barcode,
                    new ApiResponse { Code = 500, Msg = $"测试异常: {ex.Message}" },
                    null));
            }

        return results;
    }
}