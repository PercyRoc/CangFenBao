using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Common.Models;
using Common.Models.Package;
using Common.Services;
using Common.Services.Settings;
using Presentation_BenFly.Models.BenNiao;
using Presentation_BenFly.Models.Upload;
using Renci.SshNet;
using Renci.SshNet.Common;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Presentation_BenFly.Services;

/// <summary>
///     笨鸟包裹回传服务
/// </summary>
public class BenNiaoPackageService : IDisposable
{
    private const string SettingsKey = "UploadSettings";
    private UploadConfiguration _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private HttpClient _httpClient;
    private readonly ISettingsService _settingsService;

    // 创建JSON序列化选项，避免中文转义
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    private readonly BenNiaoPreReportService _preReportService;
    private readonly SemaphoreSlim _sftpSemaphore = new(1, 1);
    private bool _isDisposed;

    // SFTP客户端实例，用于连接复用
    private SftpClient? _sftpClient;

    /// <summary>
    ///     构造函数
    /// </summary>
    public BenNiaoPackageService(
        IHttpClientFactory httpClientFactory,
        ISettingsService settingsService,
        BenNiaoPreReportService preReportService)
    {
        _httpClientFactory = httpClientFactory;
        _settingsService = settingsService;
        _config = settingsService.LoadSettings<UploadConfiguration>(SettingsKey);
        _preReportService = preReportService;

        // 初始化 HttpClient
        _httpClient = CreateHttpClient();

        // 订阅配置变更
        _settingsService.OnSettingsChanged<UploadConfiguration>(HandleConfigurationChanged);

        // 异步初始化SFTP连接，不阻塞构造函数
        Task.Run(InitializeSftpConnectionAsync);
    }

    private HttpClient CreateHttpClient()
    {
        var baseUrl = _config.BenNiaoEnvironment == BenNiaoEnvironment.Production
            ? "https://api.benniao.com"
            : "http://sit.bnsy.rhb56.cn";
        
        var client = _httpClientFactory.CreateClient("BenNiao");
        client.BaseAddress = new Uri(baseUrl);
        Log.Information("已创建 HttpClient，BaseUrl: {BaseUrl}", baseUrl);
        return client;
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            // 断开SFTP连接并释放资源
            DisconnectSftpAsync().Wait();
            _sftpSemaphore.Dispose();

            // 取消配置变更订阅
            _settingsService.OnSettingsChanged<UploadConfiguration>(null);

            _isDisposed = true;
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 处理配置变更
    /// </summary>
    private async void HandleConfigurationChanged(UploadConfiguration newConfig)
    {
        try
        {
            Log.Information("笨鸟包裹回传服务配置已变更");

            var needReconnectSftp = _config.BenNiaoFtpHost != newConfig.BenNiaoFtpHost ||
                                  _config.BenNiaoFtpPort != newConfig.BenNiaoFtpPort ||
                                  _config.BenNiaoFtpUsername != newConfig.BenNiaoFtpUsername ||
                                  _config.BenNiaoFtpPassword != newConfig.BenNiaoFtpPassword;

            var needRecreateHttpClient = _config.BenNiaoEnvironment != newConfig.BenNiaoEnvironment;

            // 更新配置
            _config = newConfig;

            // 如果环境变更，重新创建 HttpClient
            if (needRecreateHttpClient)
            {
                Log.Information("笨鸟环境已变更，重新创建 HttpClient");
                _httpClient = CreateHttpClient();
            }

            // 如果SFTP配置变更，重新连接
            if (needReconnectSftp)
            {
                Log.Information("SFTP连接配置已变更，准备重新连接");
                await DisconnectSftpAsync();
                await InitializeSftpConnectionAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理配置变更时发生错误");
        }
    }

    /// <summary>
    ///     异步初始化SFTP连接
    /// </summary>
    private async Task InitializeSftpConnectionAsync()
    {
        try
        {
            // 检查SFTP配置
            if (string.IsNullOrWhiteSpace(_config.BenNiaoFtpHost) ||
                string.IsNullOrWhiteSpace(_config.BenNiaoFtpUsername) ||
                string.IsNullOrWhiteSpace(_config.BenNiaoFtpPassword))
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
            _sftpClient = new SftpClient(_config.BenNiaoFtpHost, _config.BenNiaoFtpPort,
                _config.BenNiaoFtpUsername, _config.BenNiaoFtpPassword);

            // 配置SFTP客户端超时设置
            _sftpClient.OperationTimeout = TimeSpan.FromSeconds(30);
            _sftpClient.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);

            Log.Debug("正在连接到SFTP服务器 {Host}:{Port}...", _config.BenNiaoFtpHost, _config.BenNiaoFtpPort);
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
    private static Task<bool> EnsureSftpConnectionAsync(SftpClient sftpClient)
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
    ///     处理包裹
    /// </summary>
    public async Task<bool> ProcessPackageAsync(PackageInfo package)
    {
        try
        {
            // 检查是否为Noread或空条码
            if (string.IsNullOrWhiteSpace(package.Barcode) ||
                package.Barcode.Equals("Noread", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning("包裹条码为空或Noread，将使用异常数据上传接口");

                // 在调用异步上传之前复制图像
                Image<Rgba32>? noReadImageCopy = null;
                if (package.Image != null)
                {
                    noReadImageCopy = package.Image.Clone();
                    Log.Debug("已复制异常包裹 {Barcode} 的图像用于上传", package.Barcode);
                }

                // 异步上传，不等待完成，传入复制的图像
                _ = UploadNoReadDataAsync(package, noReadImageCopy);
                return true;
            }

            // 从预报数据服务中获取预报数据
            var preReportData = _preReportService.GetPreReportData();
            var preReportItem = preReportData?.FirstOrDefault(x => x.WaybillNum == package.Barcode);

            if (preReportItem != null && !string.IsNullOrWhiteSpace(preReportItem.SegmentCode))
            {
                Log.Information("在预报数据中找到包裹 {Barcode} 的三段码：{SegmentCode}", package.Barcode, preReportItem.SegmentCode);
                package.SegmentCode = preReportItem.SegmentCode;
            }
            else
            {
                // 如果预报数据中没有，则实时查询
                var segmentCode = await GetRealTimeSegmentCodeAsync(package.Barcode);
                if (!string.IsNullOrWhiteSpace(segmentCode))
                {
                    Log.Information("通过实时查询获取到包裹 {Barcode} 的三段码：{SegmentCode}", package.Barcode, segmentCode);
                    package.SegmentCode = segmentCode;
                }
                else
                {
                    Log.Warning("无法获取包裹 {Barcode} 的三段码", package.Barcode);
                    return false;
                }
            }

            // 在调用异步上传之前复制图像
            Image<Rgba32>? packageImageCopy = null;
            if (package.Image != null)
            {
                packageImageCopy = package.Image.Clone();
                Log.Debug("已复制包裹 {Barcode} 的图像用于上传", package.Barcode);
            }

            // 恢复异步上传方式，不等待完成，传入复制的图像
            _ = UploadPackageDataAndImageAsync(package, packageImageCopy);

            Log.Information("包裹 {Barcode} 处理完成", package.Barcode);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹 {Barcode} 时发生错误", package.Barcode);
            return false;
        }
    }

    /// <summary>
    ///     实时查询三段码
    /// </summary>
    private async Task<string?> GetRealTimeSegmentCodeAsync(string waybillNum)
    {
        try
        {
            Log.Information("开始实时查询运单 {WaybillNum} 的三段码", waybillNum);

            // 记录请求参数
            var requestParams = new { waybillNum, deviceId = _config.DeviceId };
            Log.Information("实时查询三段码请求参数：{@RequestParams}", requestParams);
            Log.Information("实时查询三段码使用的AppId: {AppId}, 分拨中心: {Center}", _config.BenNiaoAppId,
                _config.BenNiaoDistributionCenterName);

            const string url = "/api/openApi/realTimeQuery";
            var request = BenNiaoSignHelper.CreateRequest(
                _config.BenNiaoAppId,
                _config.BenNiaoAppSecret,
                requestParams);

            // 序列化请求并记录日志
            var jsonContent = JsonSerializer.Serialize(request, _jsonOptions);
            Log.Information("实时查询三段码完整请求内容：{@JsonContent}", jsonContent);
            Log.Information("实时查询三段码请求地址：{BaseUrl}{Url}", _httpClient.BaseAddress, url);

            // 使用 StringContent 发送请求
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            Log.Information("开始发送请求...");
            var response = await _httpClient.PostAsync(url, content);

            // 记录响应状态和内容
            Log.Information("实时查询三段码响应状态码：{StatusCode}", response.StatusCode);
            var responseContent = await response.Content.ReadAsStringAsync();
            Log.Information("实时查询三段码响应内容：{@Response}", responseContent);

            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<BenNiaoResponse<string>>(_jsonOptions);
            if (result is { IsSuccess: true })
            {
                Log.Information("实时查询三段码成功，结果：{@Result}", result.Result);
                return result.Result;
            }

            Log.Error("实时查询三段码失败：{Message}", result?.Message);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "实时查询运单 {WaybillNum} 的三段码时发生错误", waybillNum);
            return null;
        }
    }

    /// <summary>
    ///     上传包裹数据
    /// </summary>
    private async Task<(bool Success, DateTime UploadTime)> UploadPackageDataAsync(PackageInfo package)
    {
        try
        {
            Log.Information("开始上传包裹 {Barcode} 的数据", package.Barcode);

            var uploadTime = DateTime.Now;
            const string url = "/api/openApi/dataUpload";
            var uploadItem = new DataUploadItem
            {
                NetworkName = _config.BenNiaoDistributionCenterName,
                WaybillNum = package.Barcode,
                ScanTime = uploadTime.ToString("yyyy-MM-dd HH:mm:ss"),
                Weight = (decimal)package.Weight,
                GoodsLength = package.Length.HasValue ? (int)Math.Round(package.Length.Value) : 0, // 已经是厘米，空值时为0
                GoodsWidth = package.Width.HasValue ? (int)Math.Round(package.Width.Value) : 0, // 已经是厘米，空值时为0
                GoodsHeight = package.Height.HasValue ? (int)Math.Round(package.Height.Value) : 0, // 已经是厘米，空值时为0
                DeviceId = _config.DeviceId // 添加设备号
            };

            var request = BenNiaoSignHelper.CreateRequest(
                _config.BenNiaoAppId,
                _config.BenNiaoAppSecret,
                new[] { uploadItem });

            Log.Information("上传包裹数据请求：{@Request}", JsonSerializer.Serialize(request, _jsonOptions));

            // 使用 JsonContent 替代 PostAsJsonAsync，以便使用自定义序列化选项
            var jsonContent = JsonSerializer.Serialize(request, _jsonOptions);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content);

            var responseContent = await response.Content.ReadAsStringAsync();
            Log.Information("上传包裹数据响应：{@Response}", responseContent);

            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<BenNiaoResponse<object>>(_jsonOptions);
            if (result is not { IsSuccess: true })
            {
                Log.Error("上传包裹 {Barcode} 数据失败：{Message}", package.Barcode, result?.Message);
                return (false, DateTime.MinValue);
            }

            Log.Information("成功上传包裹 {Barcode} 的数据", package.Barcode);
            return (true, uploadTime);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "上传包裹 {Barcode} 数据时发生错误", package.Barcode);
            return (false, DateTime.MinValue);
        }
    }

    /// <summary>
    ///     上传图片到SFTP服务器
    /// </summary>
    private async Task UploadImageAsync(string waybillNum, DateTime scanTime, string imagePath)
    {
        try
        {
            Log.Information("开始上传包裹 {WaybillNum} 的图片，本地路径：{ImagePath}", waybillNum, imagePath);

            // 检查本地文件
            var fileInfo = new FileInfo(imagePath);
            if (!fileInfo.Exists)
            {
                Log.Error("本地图片文件不存在：{ImagePath}", imagePath);
                return;
            }

            Log.Information("本地图片文件大小：{FileSize:N0} 字节", fileInfo.Length);

            // 检查SFTP配置
            if (string.IsNullOrWhiteSpace(_config.BenNiaoFtpHost) ||
                string.IsNullOrWhiteSpace(_config.BenNiaoFtpUsername) ||
                string.IsNullOrWhiteSpace(_config.BenNiaoFtpPassword))
            {
                Log.Warning("SFTP配置不完整，无法上传图片");
                return;
            }

            // 构建目标路径
            var dateDir = $"dws/{scanTime:yyyyMMdd}/{scanTime:HH}";
            var fileName = $"{waybillNum}_{scanTime:yyyyMMddHHmmss}.jpg";

            // 重试参数
            const int maxRetries = 3;
            const int retryDelayMs = 1000; // 重试间隔1秒
            var retryCount = 0;
            var uploadSuccessful = false;

            while (!uploadSuccessful && retryCount < maxRetries)
            {
                if (retryCount > 0)
                {
                    Log.Information("开始第 {RetryCount} 次重试上传包裹 {WaybillNum} 的图片", retryCount, waybillNum);
                    await Task.Delay(retryDelayMs * retryCount); // 递增重试延迟
                }

                // 获取复用的SFTP客户端
                var sftpClient = await GetSftpClientAsync();

                try
                {
                    // 确保连接是活跃的
                    if (!await EnsureSftpConnectionAsync(sftpClient))
                    {
                        retryCount++;
                        Log.Warning("SFTP连接未建立，尝试重试 {RetryCount}/{MaxRetries}", retryCount, maxRetries);
                        continue; // 如果连接失败，继续下一次重试
                    }

                    // 输出当前工作目录
                    try
                    {
                        var workingDirectory = sftpClient.WorkingDirectory;
                        Log.Information("当前SFTP工作目录：{WorkingDirectory}", workingDirectory);

                        // 列出根目录内容
                        var rootFiles = sftpClient.ListDirectory("/");
                        Log.Information("根目录内容：{Files}", string.Join(", ", rootFiles.Select(f => f.Name)));
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "获取SFTP目录信息时发生错误");
                    }

                    // 创建dws目录（如果不存在）
                    if (!sftpClient.Exists("/dws"))
                    {
                        Log.Debug("正在创建目录 /dws...");
                        sftpClient.CreateDirectory("/dws");
                        Log.Information("成功创建 /dws 目录");
                    }
                    else
                    {
                        Log.Debug("/dws 目录已存在");
                    }

                    // 创建日期目录和小时目录
                    var dateDirPath = $"/dws/{scanTime:yyyyMMdd}";
                    var fullDateDir = $"/{dateDir}";

                    if (!sftpClient.Exists(dateDirPath))
                    {
                        Log.Debug("正在创建目录 {DateDir}...", dateDirPath);
                        sftpClient.CreateDirectory(dateDirPath);
                        Log.Information("成功创建 {DateDir} 目录", dateDirPath);
                    }

                    if (!sftpClient.Exists(fullDateDir))
                    {
                        Log.Debug("正在创建小时目录 {HourDir}...", fullDateDir);
                        sftpClient.CreateDirectory(fullDateDir);
                        Log.Information("成功创建小时目录 {HourDir}", fullDateDir);
                    }
                    else
                    {
                        Log.Debug("{DateDir} 目录已存在", fullDateDir);
                    }

                    // 上传文件
                    var remotePath = $"/{dateDir}/{fileName}";
                    Log.Debug("开始上传文件到 {RemotePath}...", remotePath);
                    await using var fileStream = File.OpenRead(imagePath);

                    // 记录上传开始时间
                    var startTime = DateTime.Now;
                    sftpClient.UploadFile(fileStream, remotePath);
                    var endTime = DateTime.Now;

                    // 验证上传结果
                    if (sftpClient.Exists(remotePath))
                    {
                        var uploadedFileInfo = sftpClient.GetAttributes(remotePath);
                        Log.Information("文件上传成功 - 路径：{RemotePath}, 大小：{Size:N0} 字节, 耗时：{Duration:N0}ms",
                            remotePath,
                            uploadedFileInfo.Size,
                            (endTime - startTime).TotalMilliseconds);

                        // 验证文件大小
                        if (uploadedFileInfo.Size == fileInfo.Length)
                        {
                            Log.Information("文件大小验证成功，本地和远程文件大小一致");
                            uploadSuccessful = true;
                        }
                        else
                        {
                            Log.Warning("文件大小不匹配 - 本地：{LocalSize:N0} 字节, 远程：{RemoteSize:N0} 字节",
                                fileInfo.Length, uploadedFileInfo.Size);
                        }
                    }
                    else
                    {
                        Log.Warning("文件上传后未在服务器上找到：{RemotePath}", remotePath);
                    }
                }
                catch (Exception ex)
                {
                    retryCount++;

                    // 连接问题时，重置SFTP客户端以便下次重试创建新连接
                    if (ex is SshConnectionException || ex is IOException)
                    {
                        Log.Error(ex, "SFTP连接错误，将断开连接并在下次重试时重新连接");
                        await DisconnectSftpAsync(); // 强制断开当前连接
                    }
                    else
                    {
                        Log.Error(ex, "上传文件时发生错误");
                    }

                    if (retryCount >= maxRetries)
                        Log.Error("上传包裹 {WaybillNum} 的图片在 {RetryCount} 次尝试后失败", waybillNum, retryCount);
                    else
                        Log.Warning("上传包裹 {WaybillNum} 的图片时发生错误，将尝试重试 ({RetryCount}/{MaxRetries})",
                            waybillNum, retryCount, maxRetries);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "上传包裹 {WaybillNum} 的图片时发生错误", waybillNum);
        }
    }

    /// <summary>
    ///     将图片保存到临时文件
    /// </summary>
    private static async Task<string?> SaveImageToTempFileAsync(Image<Rgba32> image, string waybillNum,
        DateTime scanTime)
    {
        try
        {
            // 创建临时文件路径
            var tempDir = Path.Combine(Path.GetTempPath(), "BenNiao", "Images");
            Directory.CreateDirectory(tempDir);

            var fileName = $"{waybillNum}_{scanTime:yyyyMMddHHmmss}.jpg";
            var tempPath = Path.Combine(tempDir, fileName);

            // 保存图片
            await image.SaveAsJpegAsync(tempPath);
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
    ///     异步上传包裹数据和图片
    /// </summary>
    private async Task UploadPackageDataAndImageAsync(PackageInfo package, Image<Rgba32>? imageCopy)
    {
        try
        {
            var (success, uploadTime) = await UploadPackageDataAsync(package);
            if (success && imageCopy != null)
                try
                {
                    // 使用复制的图像进行上传
                    var tempImagePath = await SaveImageToTempFileAsync(imageCopy, package.Barcode, uploadTime);
                    if (!string.IsNullOrWhiteSpace(tempImagePath))
                    {
                        await UploadImageAsync(package.Barcode, uploadTime, tempImagePath);
                        try
                        {
                            File.Delete(tempImagePath);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "删除临时图片文件 {TempImagePath} 失败", tempImagePath);
                        }
                    }
                }
                finally
                {
                    // 确保复制的图像被释放
                    imageCopy.Dispose();
                    Log.Debug("已释放包裹 {Barcode} 的复制图像", package.Barcode);
                }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "异步上传包裹 {Barcode} 数据和图片时发生错误", package.Barcode);
        }
    }

    /// <summary>
    ///     上传异常数据（Noread或空条码）
    /// </summary>
    private async Task<bool> UploadNoReadDataAsync(PackageInfo package, Image<Rgba32>? imageCopy)
    {
        try
        {
            Log.Information("开始上传异常包裹数据");
            var uploadTime = DateTime.Now;
            const string url = "/api/openApi/noReadDataUpload";

            // 将图片转换为Base64
            string? base64Image = null;
            if (imageCopy != null)
                try
                {
                    using var memoryStream = new MemoryStream();
                    await imageCopy.SaveAsJpegAsync(memoryStream);
                    base64Image = Convert.ToBase64String(memoryStream.ToArray());
                }
                finally
                {
                    imageCopy.Dispose();
                    Log.Debug("已释放异常包裹的复制图像");
                }

            var uploadItem = new
            {
                netWorkName = _config.BenNiaoDistributionCenterName,
                deviceId = _config.DeviceId,
                waybillNum = package.Barcode, // 可以为空
                scanTime = uploadTime.ToString("yyyy-MM-dd HH:mm:ss"),
                weight = (decimal)package.Weight, // 单位为kg
                goodsLength = package.Length.HasValue ? (int)Math.Round(package.Length.Value) : 0, // 已经是厘米，空值时为0
                goodsWidth = package.Width.HasValue ? (int)Math.Round(package.Width.Value) : 0, // 已经是厘米，空值时为0
                goodsHeight = package.Height.HasValue ? (int)Math.Round(package.Height.Value) : 0, // 已经是厘米，空值时为0
                picture = base64Image // Base64格式图片
            };

            var request = BenNiaoSignHelper.CreateRequest(
                _config.BenNiaoAppId,
                _config.BenNiaoAppSecret,
                new[] { uploadItem });

            // 使用 JsonContent 替代 PostAsJsonAsync，以便使用自定义序列化选项
            var jsonContent = JsonSerializer.Serialize(request, _jsonOptions);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content);

            var responseContent = await response.Content.ReadAsStringAsync();
            Log.Information("上传异常包裹数据响应：{@Response}", responseContent);

            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<BenNiaoResponse<object>>(_jsonOptions);
            if (result is not { IsSuccess: true })
            {
                Log.Error("上传异常包裹数据失败：{Message}", result?.Message);
                return false;
            }

            Log.Information("成功上传异常包裹数据");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "上传异常包裹数据时发生错误");
            return false;
        }
    }
}