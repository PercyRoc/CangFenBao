using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Media.Imaging;
using BenFly.Models.BenNiao;
using BenFly.Models.Upload;
using Common.Models.Package;
using Common.Services.Settings;
using Renci.SshNet;
using Renci.SshNet.Common;
using Serilog;

namespace BenFly.Services;

/// <summary>
///     笨鸟包裹回传服务
/// </summary>
internal class BenNiaoPackageService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly IHttpClientFactory _httpClientFactory;

    // 创建JSON序列化选项，避免中文转义
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    private readonly ISettingsService _settingsService;

    private readonly SemaphoreSlim _sftpSemaphore = new(1, 1);
    private bool _isDisposed;

    // SFTP客户端实例，用于连接复用
    private SftpClient? _sftpClient;

    /// <summary>
    ///     构造函数
    /// </summary>
    public BenNiaoPackageService(
        IHttpClientFactory httpClientFactory,
        ISettingsService settingsService)
    {
        _httpClientFactory = httpClientFactory;
        _settingsService = settingsService;

        // 初始化 HttpClient
        _httpClient = CreateHttpClient();

        // 异步初始化SFTP连接，不阻塞构造函数
        Task.Run(InitializeSftpConnectionAsync);
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            // 断开SFTP连接并释放资源
            DisconnectSftpAsync().Wait();
            _sftpSemaphore.Dispose();

            _isDisposed = true;
        }

        GC.SuppressFinalize(this);
    }

    private HttpClient CreateHttpClient()
    {
        var baseUrl = _settingsService.LoadSettings<UploadConfiguration>().BenNiaoEnvironment ==
                      BenNiaoEnvironment.Production
            ? "https://bnsy.benniaosuyun.com"
            : "https://sit.bnsy.rhb56.cn";

        var client = _httpClientFactory.CreateClient("BenNiao");
        client.BaseAddress = new Uri(baseUrl);
        Log.Information("已创建 HttpClient，BaseUrl: {BaseUrl}", baseUrl);
        return client;
    }

    /// <summary>
    ///     异步初始化SFTP连接
    /// </summary>
    private async Task InitializeSftpConnectionAsync()
    {
        try
        {
            // 检查SFTP配置
            if (string.IsNullOrWhiteSpace(_settingsService.LoadSettings<UploadConfiguration>().BenNiaoFtpHost) ||
                string.IsNullOrWhiteSpace(_settingsService.LoadSettings<UploadConfiguration>().BenNiaoFtpUsername) ||
                string.IsNullOrWhiteSpace(_settingsService.LoadSettings<UploadConfiguration>().BenNiaoFtpPassword))
            {
                Log.Warning("SFTP配置不完整，跳过初始化SFTP连接");
                return;
            }

            Log.Information("服务启动时初始化SFTP连接...");
            await GetSftpClientAsync(); // 此方法会建立SFTP连接
            Log.Information("SFTP连接初始化完成");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化SFTP连接失败，将在第一次上传时重试");
        }
    }

    /// <summary>
    ///     初始化并连接SFTP客户端（如果尚未连接）
    /// </summary>
    private async Task<SftpClient> GetSftpClientAsync()
    {
        await _sftpSemaphore.WaitAsync();
        try
        {
            // 如果客户端不存在或已断开连接，则创建并连接
            if (_sftpClient is { IsConnected: true }) return _sftpClient;

            // 如果存在旧客户端，安全释放它
            if (_sftpClient != null)
                try
                {
                    _sftpClient.Disconnect();
                    _sftpClient.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "释放旧的SFTP客户端时发生错误");
                }

            // 创建新客户端
            _sftpClient = new SftpClient(_settingsService.LoadSettings<UploadConfiguration>().BenNiaoFtpHost,
                _settingsService.LoadSettings<UploadConfiguration>().BenNiaoFtpPort,
                _settingsService.LoadSettings<UploadConfiguration>().BenNiaoFtpUsername,
                _settingsService.LoadSettings<UploadConfiguration>().BenNiaoFtpPassword)
            {
                // 配置SFTP客户端超时设置
                OperationTimeout = TimeSpan.FromSeconds(30)
            };
            _sftpClient.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);

            Log.Debug("正在连接到SFTP服务器 {Host}:{Port}...",
                _settingsService.LoadSettings<UploadConfiguration>().BenNiaoFtpHost,
                _settingsService.LoadSettings<UploadConfiguration>().BenNiaoFtpPort);
            _sftpClient.Connect();
            Log.Debug("已成功连接到SFTP服务器");

            return _sftpClient;
        }
        finally
        {
            _sftpSemaphore.Release();
        }
    }

    /// <summary>
    ///     断开SFTP连接
    /// </summary>
    private async Task DisconnectSftpAsync()
    {
        await _sftpSemaphore.WaitAsync();
        try
        {
            if (_sftpClient is { IsConnected: true })
                try
                {
                    Log.Debug("断开SFTP连接...");
                    _sftpClient.Disconnect();
                    _sftpClient.Dispose();
                    Log.Debug("SFTP连接已断开");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "断开SFTP连接时发生错误");
                }
                finally
                {
                    _sftpClient = null;
                }
        }
        finally
        {
            _sftpSemaphore.Release();
        }
    }

    // 检查SFTP连接是否活跃并尝试重新连接
    private static Task<bool> EnsureSftpConnectionAsync(BaseClient sftpClient)
    {
        if (sftpClient.IsConnected) return Task.FromResult(true);

        try
        {
            Log.Information("SFTP连接已断开，尝试重新连接...");
            sftpClient.Connect();
            return Task.FromResult(sftpClient.IsConnected);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "重新连接SFTP服务器失败");
            return Task.FromResult(false);
        }
    }

    /// <summary>
    ///     实时查询三段码
    /// </summary>
    /// <returns>包含段码和错误消息的元组。如果成功，ErrorMessage 为 null。</returns>
    internal async Task<(string? SegmentCode, string? ErrorMessage)> GetRealTimeSegmentCodeAsync(string waybillNum,
        CancellationToken cancellationToken)
    {
        try
        {
            Log.Information("开始实时查询运单 {WaybillNum} 的三段码", waybillNum);

            // 记录请求参数
            var requestParams = new
            {
                waybillNum,
                deviceId = _settingsService.LoadSettings<UploadConfiguration>().DeviceId
            };

            const string url = "/api/openApi/realTimeQuery";
            var request = BenNiaoSignHelper.CreateRequest(
                _settingsService.LoadSettings<UploadConfiguration>().BenNiaoAppId,
                _settingsService.LoadSettings<UploadConfiguration>().BenNiaoAppSecret,
                requestParams);

            // 序列化请求并记录日志
            var jsonContent = JsonSerializer.Serialize(request, _jsonOptions);

            // 使用 StringContent 发送请求
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content, cancellationToken);
            // 不再 EnsureSuccessStatusCode，手动检查并返回错误
            // response.EnsureSuccessStatusCode(); 

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                var httpErrorMessage =
                    $"HTTP请求失败: {(int)response.StatusCode} {response.ReasonPhrase}. 内容: {errorContent}";
                Log.Error("实时查询三段码失败: {ErrorMessage}", httpErrorMessage);
                return (null, httpErrorMessage);
            }

            var result =
                await response.Content.ReadFromJsonAsync<BenNiaoResponse<string>>(_jsonOptions, cancellationToken);
            if (result is { IsSuccess: true }) return (result.Result, null); // 成功，无错误消息

            var apiErrorMessage = result?.Message ?? "API返回未知错误";
            Log.Error("实时查询三段码失败：{Message}", apiErrorMessage);
            return (null, $"API错误: {apiErrorMessage}"); // API逻辑失败
        }
        catch (Exception ex)
        {
            var exceptionMessage = $"查询段码异常: {ex.Message}";
            Log.Error(ex, "实时查询运单 {WaybillNum} 的三段码时发生错误", waybillNum);
            return (null, exceptionMessage); // 捕获到异常
        }
    }

    /// <summary>
    ///     上传包裹数据
    /// </summary>
    internal async Task<(bool Success, DateTime UploadTime, string ErrorMessage)> UploadPackageDataAsync(
        PackageInfo package, CancellationToken cancellationToken)
    {
        try
        {
            Log.Information("开始上传包裹 {Barcode} 的数据", package.Barcode);

            var uploadTime = DateTime.Now;
            const string url = "/api/openApi/dataUpload";
            var uploadItem = new DataUploadItem
            {
                NetworkName = _settingsService.LoadSettings<UploadConfiguration>().BenNiaoDistributionCenterName,
                WaybillNum = package.Barcode,
                ScanTime = uploadTime.ToString("yyyy-MM-dd HH:mm:ss"),
                Weight = (decimal)package.Weight,
                GoodsLength = package.Length.HasValue ? (int)Math.Round(package.Length.Value) : 0, // 已经是厘米，空值时为0
                GoodsWidth = package.Width.HasValue ? (int)Math.Round(package.Width.Value) : 0, // 已经是厘米，空值时为0
                GoodsHeight = package.Height.HasValue ? (int)Math.Round(package.Height.Value) : 0, // 已经是厘米，空值时为0
                DeviceId = _settingsService.LoadSettings<UploadConfiguration>().DeviceId // 添加设备号
            };

            var request = BenNiaoSignHelper.CreateRequest(
                _settingsService.LoadSettings<UploadConfiguration>().BenNiaoAppId,
                _settingsService.LoadSettings<UploadConfiguration>().BenNiaoAppSecret,
                new[]
                {
                    uploadItem
                });


            // 使用 JsonContent 替代 PostAsJsonAsync，以便使用自定义序列化选项
            var jsonContent = JsonSerializer.Serialize(request, _jsonOptions);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content, cancellationToken);
            response.EnsureSuccessStatusCode();

            var result =
                await response.Content.ReadFromJsonAsync<BenNiaoResponse<object>>(_jsonOptions, cancellationToken);
            if (result is { IsSuccess: true }) return (true, uploadTime, string.Empty);

            var errorMessage = result?.Message ?? "未知错误";
            Log.Error("上传包裹 {Barcode} 数据失败：{Message}", package.Barcode, errorMessage);
            return (false, DateTime.MinValue, $"API返回错误: {errorMessage}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "上传包裹 {Barcode} 数据时发生错误", package.Barcode);
            return (false, DateTime.MinValue, $"上传异常: {ex.Message}");
        }
    }

    /// <summary>
    ///     上传图片到SFTP服务器
    /// </summary>
    /// <returns>包含成功状态和错误消息的元组。如果成功，ErrorMessage 为 null。</returns>
    internal async Task<(bool Success, string? ErrorMessage)> UploadImageAsync(string waybillNum, DateTime scanTime,
        string imagePath)
    {
        string? currentErrorMessage = null;
        try
        {
            Log.Information("开始上传包裹 {WaybillNum} 的图片，本地路径：{ImagePath}", waybillNum, imagePath);

            // 检查本地文件
            var fileInfo = new FileInfo(imagePath);
            if (!fileInfo.Exists)
            {
                currentErrorMessage = $"本地图片文件不存在: {imagePath}";
                Log.Error(currentErrorMessage);
                return (false, currentErrorMessage);
            }

            Log.Information("本地图片文件大小：{FileSize:N0} 字节", fileInfo.Length);

            // 检查SFTP配置
            if (string.IsNullOrWhiteSpace(_settingsService.LoadSettings<UploadConfiguration>().BenNiaoFtpHost) ||
                string.IsNullOrWhiteSpace(_settingsService.LoadSettings<UploadConfiguration>().BenNiaoFtpUsername) ||
                string.IsNullOrWhiteSpace(_settingsService.LoadSettings<UploadConfiguration>().BenNiaoFtpPassword))
            {
                currentErrorMessage = "SFTP配置不完整，无法上传图片";
                Log.Warning(currentErrorMessage);
                return (false, currentErrorMessage);
            }

            // 构建目标路径
            var dateDir = $"dws/{scanTime:yyyyMMdd}/{scanTime:HH}";
            var fileName = $"{waybillNum}_{scanTime:yyyyMMddHHmmss}.jpg";

            // 重试参数
            const int maxRetries = 3;
            const int retryDelayMs = 1000;
            var retryCount = 0;
            var uploadSuccessful = false;

            while (!uploadSuccessful && retryCount < maxRetries)
            {
                if (retryCount > 0)
                {
                    Log.Information("开始第 {RetryCount} 次重试上传包裹 {WaybillNum} 的图片", retryCount, waybillNum);
                    await Task.Delay(retryDelayMs * retryCount);
                }

                try
                {
                    // 获取复用的SFTP客户端
                    var sftpClient = await GetSftpClientAsync(); // 在循环内声明

                    // 确保连接是活跃的
                    if (!await EnsureSftpConnectionAsync(sftpClient))
                    {
                        retryCount++;
                        currentErrorMessage = $"SFTP连接失败 (尝试 {retryCount}/{maxRetries})";
                        Log.Warning("SFTP连接未建立，尝试重试 {RetryCount}/{MaxRetries}", retryCount, maxRetries);
                        continue;
                    }

                    // 输出当前工作目录
                    try
                    {
                        // 列出根目录内容
                        sftpClient.ListDirectory("/");
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "获取SFTP目录信息时发生错误");
                    }

                    // 创建dws目录（如果不存在）
                    if (!await sftpClient.ExistsAsync("/dws"))
                        await sftpClient.CreateDirectoryAsync("/dws");
                    else
                        Log.Debug("/dws 目录已存在");

                    // 创建日期目录和小时目录
                    var dateDirPath = $"/dws/{scanTime:yyyyMMdd}";
                    var fullDateDir = $"/{dateDir}";

                    if (!await sftpClient.ExistsAsync(dateDirPath)) await sftpClient.CreateDirectoryAsync(dateDirPath);

                    if (!await sftpClient.ExistsAsync(fullDateDir))
                        await sftpClient.CreateDirectoryAsync(fullDateDir);
                    else
                        Log.Debug("{DateDir} 目录已存在", fullDateDir);

                    // 上传文件
                    var remotePath = $"/{dateDir}/{fileName}";
                    await using var fileStream = File.OpenRead(imagePath);

                    var startTime = DateTime.Now;
                    sftpClient.UploadFile(fileStream, remotePath);
                    var endTime = DateTime.Now;

                    if (await sftpClient.ExistsAsync(remotePath))
                    {
                        var uploadedFileInfo = sftpClient.GetAttributes(remotePath);
                        Log.Information("文件上传成功 - 路径：{RemotePath}, 大小：{Size:N0} 字节, 耗时：{Duration:N0}ms",
                            remotePath, uploadedFileInfo.Size, (endTime - startTime).TotalMilliseconds);

                        if (uploadedFileInfo.Size == fileInfo.Length)
                        {
                            Log.Information("文件大小验证成功");
                            uploadSuccessful = true;
                            currentErrorMessage = null; // 清除之前的错误信息
                        }
                        else
                        {
                            currentErrorMessage = $"文件大小不匹配 - 本地: {fileInfo.Length}, 远程: {uploadedFileInfo.Size}";
                            Log.Warning(currentErrorMessage);
                            // 文件大小不匹配也视为失败，以便重试
                            retryCount++;
                        }
                    }
                    else
                    {
                        currentErrorMessage = $"文件上传后未在服务器上找到: {remotePath}";
                        Log.Warning(currentErrorMessage);
                        retryCount++;
                    }
                }
                catch (Exception ex)
                {
                    retryCount++;
                    currentErrorMessage = $"SFTP上传/检查时异常 (尝试 {retryCount}/{maxRetries}): {ex.Message}";
                    Log.Error(ex, "上传文件时发生错误 (尝试 {RetryCount}/{MaxRetries})", retryCount, maxRetries);

                    if (ex is SshConnectionException or IOException)
                    {
                        Log.Warning("SFTP连接错误，将断开连接并在下次重试时重新连接");
                        await DisconnectSftpAsync();
                    }

                    if (retryCount >= maxRetries)
                    {
                        currentErrorMessage = $"图片上传重试 {maxRetries} 次后失败: {ex.Message}";
                        Log.Error("上传包裹 {WaybillNum} 的图片在 {RetryCount} 次尝试后失败", waybillNum, retryCount);
                    }
                }
            } // end while

            return (uploadSuccessful, currentErrorMessage);
        }
        catch (Exception ex)
        {
            currentErrorMessage = $"图片上传外部异常: {ex.Message}";
            Log.Error(ex, "上传包裹 {WaybillNum} 的图片时发生外部错误", waybillNum);
            return (false, currentErrorMessage);
        }
    }

    /// <summary>
    ///     将图片保存到临时文件
    /// </summary>
    public string? SaveImageToTempFileAsync(BitmapSource image, string waybillNum, DateTime scanTime,
        PackageInfo package)
    {
        try
        {
            // 创建临时文件路径
            var tempDir = Path.Combine(Path.GetTempPath(), "BenNiao", "Images");
            Directory.CreateDirectory(tempDir);

            var fileName = $"{waybillNum}_{scanTime:yyyyMMddHHmmss}.jpg";
            var tempPath = Path.Combine(tempDir, fileName);

            // 将 BitmapSource 转换为 Bitmap (System.Drawing.Bitmap)
            using var bitmap = ConvertBitmapSourceToBitmap(image);
            if (bitmap == null)
            {
                Log.Error("无法将 BitmapSource 转换为 System.Drawing.Bitmap，无法添加水印。");
                return null;
            }

            // 获取设备号
            var uploadConfig = _settingsService.LoadSettings<UploadConfiguration>();
            var deviceId = uploadConfig.DeviceId;

            // 添加水印
            AddWatermarkToImage(bitmap, package, deviceId);

            // 保存添加水印后的图片
            bitmap.Save(tempPath, ImageFormat.Jpeg);

            Log.Information("已将包裹 {WaybillNum} 的图片保存到临时文件 {TempPath}", waybillNum, tempPath);

            return tempPath;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存包裹 {WaybillNum} 的图片到临时文件时发生错误", waybillNum);
            return null;
        }
    }

    /// <summary>
    ///     将 BitmapSource 转换为 System.Drawing.Bitmap
    /// </summary>
    /// <param name="bitmapSource">要转换的 BitmapSource</param>
    /// <returns>转换后的 Bitmap</returns>
    private static Bitmap? ConvertBitmapSourceToBitmap(BitmapSource bitmapSource)
    {
        try
        {
            // 使用流和编码器进行可靠的格式转换，避免手动操作像素数据导致的格式问题。
            // BmpBitmapEncoder 是一种快速的无损编码方式，适合作为中间格式。
            var encoder = new BmpBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmapSource));

            using var stream = new MemoryStream();
            encoder.Save(stream);
            stream.Position = 0; // Rewind the stream

            // 从流创建一个临时的 Bitmap。
            // 直接从流创建的Bitmap可能由于其像素格式（如索引格式）而不支持GDI+绘图。
            using var tempBitmap = new Bitmap(stream);

            // 为了确保返回的Bitmap是可编辑的（支持GDI+绘图），我们创建一个副本。
            // new Bitmap(Image) 会创建一个具有标准像素格式（如 32bpp ARGB）的可写副本。
            return new Bitmap(tempBitmap);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "将 BitmapSource 转换为 System.Drawing.Bitmap 时发生错误");
            return null;
        }
    }


    /// <summary>
    ///     给图片添加水印
    /// </summary>
    /// <param name="image">要添加水印的图片</param>
    /// <param name="package">包裹信息</param>
    /// <param name="deviceId">设备号</param>
    private static void AddWatermarkToImage(Bitmap image, PackageInfo package, string deviceId)
    {
        try
        {
            using var graphics = Graphics.FromImage(image);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            // 设置字体和颜色
            var font = new Font("Arial", 20, FontStyle.Bold); // 增大字体
            var brush = new SolidBrush(Color.Red); // 红色字体

            // 构建水印文本
            var watermarkText = new StringBuilder();
            watermarkText.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            watermarkText.AppendLine($"条码: {package.Barcode}");
            watermarkText.AppendLine($"重量: {package.Weight:F2} kg");
            watermarkText.AppendLine($"尺寸: {package.Length:F0}x{package.Width:F0}x{package.Height:F0} cm");
            watermarkText.AppendLine($"设备号: {deviceId}");

            // 计算文本大小和位置
            var lines = watermarkText.ToString().Split([
                "\r\n", "\n"
            ], StringSplitOptions.None); // Split by newline
            var lineHeight = font.GetHeight(graphics);
            var yPosition = 10; // 初始Y坐标

            foreach (var line in lines)
            {
                graphics.MeasureString(line, font);
                const int xPosition = 10; // 距离左边10像素

                graphics.DrawString(line, font, brush, xPosition, yPosition);
                yPosition += (int)lineHeight + 5; // 每行增加5像素间距
            }

            Log.Information("已为图片添加水印。");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "为图片添加水印时发生错误。");
        }
    }


    /// <summary>
    ///     上传异常数据（Noread或空条码）
    /// </summary>
    /// <returns>包含成功状态和错误消息的元组。如果成功，ErrorMessage 为 null。</returns>
    internal async Task<(bool Success, string? ErrorMessage)> UploadNoReadDataAsync(PackageInfo package,
        BitmapSource? imageCopy)
    {
        try
        {
            Log.Information("开始上传异常包裹数据");
            var uploadTime = DateTime.Now;
            const string url = "/api/openApi/noReadDataUpload";

            // 将图片转换为Base64
            string? base64Image = null;
            if (imageCopy != null)
            {
                using var memoryStream = new MemoryStream();
                var encoder = new JpegBitmapEncoder
                {
                    QualityLevel = 90
                };
                encoder.Frames.Add(BitmapFrame.Create(imageCopy));
                encoder.Save(memoryStream);
                base64Image = Convert.ToBase64String(memoryStream.ToArray());
                Log.Debug("已将异常包裹的图像转换为Base64");
            }

            var uploadItem = new
            {
                netWorkName = _settingsService.LoadSettings<UploadConfiguration>().BenNiaoDistributionCenterName,
                deviceId = _settingsService.LoadSettings<UploadConfiguration>().DeviceId,
                waybillNum = package.Barcode, // 可以为空
                scanTime = uploadTime.ToString("yyyy-MM-dd HH:mm:ss"),
                weight = (decimal)package.Weight, // 单位为kg
                goodsLength = package.Length.HasValue ? (int)Math.Round(package.Length.Value) : 0, // 已经是厘米，空值时为0
                goodsWidth = package.Width.HasValue ? (int)Math.Round(package.Width.Value) : 0, // 已经是厘米，空值时为0
                goodsHeight = package.Height.HasValue ? (int)Math.Round(package.Height.Value) : 0, // 已经是厘米，空值时为0
                picture = base64Image // Base64格式图片
            };

            var request = BenNiaoSignHelper.CreateRequest(
                _settingsService.LoadSettings<UploadConfiguration>().BenNiaoAppId,
                _settingsService.LoadSettings<UploadConfiguration>().BenNiaoAppSecret,
                new[]
                {
                    uploadItem
                });

            var jsonContent = JsonSerializer.Serialize(request, _jsonOptions);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content);

            var responseContent = await response.Content.ReadAsStringAsync();
            Log.Information("上传异常包裹数据响应：{@Response}", responseContent);

            // 不再 EnsureSuccessStatusCode，手动检查并返回错误
            // response.EnsureSuccessStatusCode(); 

            if (!response.IsSuccessStatusCode)
            {
                var httpErrorMessage =
                    $"HTTP请求失败: {(int)response.StatusCode} {response.ReasonPhrase}. 内容: {responseContent}";
                Log.Error("上传异常包裹数据失败: {ErrorMessage}", httpErrorMessage);
                return (false, httpErrorMessage);
            }

            var result = await response.Content.ReadFromJsonAsync<BenNiaoResponse<object>>(_jsonOptions);
            if (result is not { IsSuccess: true })
            {
                var apiErrorMessage = result?.Message ?? "API返回未知错误";
                Log.Error("上传异常包裹数据失败：{Message}", apiErrorMessage);
                return (false, $"API错误: {apiErrorMessage}"); // API逻辑失败
            }

            Log.Information("成功上传异常包裹数据");
            return (true, null); // 成功，无错误消息
        }
        catch (Exception ex)
        {
            var exceptionMessage = $"上传NoRead数据异常: {ex.Message}";
            Log.Error(ex, "上传异常包裹数据时发生错误");
            return (false, exceptionMessage); // 捕获到异常
        }
    }
}