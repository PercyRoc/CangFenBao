using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Common.Models.Package;
using Common.Services.Settings;
using Common.Services.Ui;
using KuaiLv.Models.DWS;
using KuaiLv.Models.Settings.App;
using KuaiLv.Models.Settings.Upload;
using KuaiLv.Services.Warning;
using Serilog;

namespace KuaiLv.Services.DWS;

/// <summary>
///     DWS服务实现
/// </summary>
internal class DwsService : IDwsService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Timer _networkCheckTimer;
    private readonly INotificationService _notificationService;
    private readonly Timer _offlinePackageRetryTimer;
    private readonly OfflinePackageService _offlinePackageService;
    private readonly ISettingsService _settingsService;
    private readonly IWarningLightService _warningLightService;
    private UploadConfiguration _currentConfig;

    private bool _disposed;
    private bool _isNetworkAvailable = true;

    public DwsService(
        HttpClient httpClient,
        ISettingsService settingsService,
        INotificationService notificationService,
        IWarningLightService warningLightService,
        OfflinePackageService offlinePackageService)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
        _notificationService = notificationService;
        _warningLightService = warningLightService;
        _offlinePackageService = offlinePackageService;

        // 从设置服务加载配置
        _currentConfig = settingsService.LoadSettings<UploadConfiguration>();

        // 订阅配置变更事件
        settingsService.OnSettingsChanged<UploadConfiguration>(OnConfigurationChanged);

        // 初始化网络检测定时器（每30秒检查一次）
        _networkCheckTimer = new Timer(CheckNetworkStatus, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));

        // 初始化离线包裹重试定时器（每1分钟检查一次）
        _offlinePackageRetryTimer = new Timer(RetryOfflinePackages, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
    }

    /// <summary>
    ///     释放资源
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public async Task<DwsResponse> ReportPackageAsync(PackageInfo package)
    {
        try
        {
            if (!_isNetworkAvailable)
            {
                Log.Warning("网络未连接，保存包裹到离线存储：{Barcode}", package.Barcode);
                await _offlinePackageService.SaveOfflinePackageAsync(package);
                package.SetError("网络未连接，已保存到离线存储");
                return new DwsResponse { Code = 200, Message = "包裹已保存到离线存储" };
            }

            // 使用当前配置
            var config = _currentConfig;

            // 获取当前操作模式
            var appSettings = _settingsService.LoadSettings<AppSettings>();
            var operationMode = appSettings.OperationMode + 1; // 加1是因为UI从0开始，接口从1开始
            Log.Debug("当前操作场景：{OperateScene}", operationMode);

            // 构建请求数据
            var request = new DwsRequest
            {
                BarCode = string.IsNullOrEmpty(package.Barcode) ? "noread" : package.Barcode,
                Weight = Math.Round(package.Weight, 2),
                Length = package.Length ?? 0,
                Width = package.Width ?? 0,
                Height = package.Height ?? 0,
                Volume = package.Volume ?? 0,
                Timestamp = package.CreateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                OperateScene = operationMode // 设置操作场景
            };

            // 序列化请求数据
            var jsonContent = JsonSerializer.Serialize(request);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            // 计算Content-MD5 - 正确计算一次并使用Base64格式
            var contentMd5 = Convert.ToBase64String(MD5.HashData(Encoding.UTF8.GetBytes(jsonContent)));
            content.Headers.ContentMD5 = Convert.FromBase64String(contentMd5);

            // 构建签名字符串 - 确保路径一致
            var timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString();
            // 统一使用相同的API路径
            const string path = "/haina/parcel/dws/w/bindWeight";
            var stringToSign = $"POST\n{contentMd5}\n{path}";

            // 计算签名
            var secret = config.Secret;
            var signature = Convert.ToBase64String(new HMACSHA256(Encoding.UTF8.GetBytes(secret))
                .ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

            // 设置基地址 - 保持不变
            var baseAddress = config.Environment == UploadEnvironment.Production
                ? "https://klwms.meituan.com"
                : "https://klvwms.meituan.com";

            // 记录请求前的信息
            Log.Information("DWS请求参数: BaseUrl={BaseUrl}, Path={Path}, Method={Method}",
                baseAddress, path, "POST");
            Log.Information("DWS请求头: Accept={Accept}, App={App}, Timestamp={Timestamp}, MD5={MD5}",
                "application/json", "kl_dws_weighing", timestamp, contentMd5);
            Log.Information("DWS请求体: {RequestBody}", jsonContent);

            // 显式创建HttpRequestMessage确保使用POST方法
            // 使用完整URL
            var fullUrl = new Uri($"{baseAddress}{path}");
            Log.Information("DWS完整请求URL: {FullUrl}", fullUrl);

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, fullUrl)
            {
                Content = content
            };

            // 设置请求头部
            requestMessage.Headers.Add("Accept", "application/json");
            requestMessage.Headers.Add("S-Ca-App", "kl_dws_weighing");
            requestMessage.Headers.Add("S-Ca-Timestamp", timestamp);
            requestMessage.Headers.Add("S-Ca-Signature", signature);

            // 发送请求，不使用默认请求头
            Log.Information("开始发送DWS请求...");
            var response = await _httpClient.SendAsync(requestMessage);
            Log.Information("DWS响应状态码: {StatusCode}", response.StatusCode);

            var responseContent = await response.Content.ReadAsStringAsync();
            Log.Information("DWS响应内容: {ResponseContent}", responseContent);

            var result = JsonSerializer.Deserialize<DwsResponse>(responseContent);

            if (result == null)
            {
                Log.Error("DWS服务响应解析失败");
                _notificationService.ShowError("DWS服务异常");
                return new DwsResponse { Code = 500, Message = "服务器响应解析失败" };
            }

            // 处理不同的响应状态
            switch (result.Code)
            {
                case 200 when string.IsNullOrEmpty(result.Message):
                    // 包裹为SKU多退少补品
                    Log.Information("包裹上报成功：{Barcode}", package.Barcode);
                    _notificationService.ShowSuccess("包裹上报成功");
                    await _warningLightService.ShowGreenLightAsync();
                    package.StatusDisplay = "成功";
                    package.Information = "上报成功：多退少补品";
                    return result;

                case 200:
                    // 包裹为SKU非多退少补品
                    Log.Information("包裹上报成功：{Barcode}", package.Barcode);
                    _notificationService.ShowSuccess("包裹上报成功");
                    await _warningLightService.ShowGreenLightAsync();
                    package.StatusDisplay = "成功";
                    package.Information = result.Message ?? "上报成功";
                    return result;

                case 1003:
                    // 包裹号无效
                    Log.Warning("包裹号无效：{Barcode}", package.Barcode);
                    _notificationService.ShowWarning("包裹号无效");
                    await _warningLightService.TurnOffGreenLightAsync();
                    await Task.Delay(100); // 短暂延时确保绿灯完全关闭
                    await _warningLightService.ShowRedLightAsync();
                    package.SetError($"包裹号无效：{result.Message}");
                    break;

                case 1005:
                    // 非履约日包裹
                    Log.Warning("非履约日包裹：{Barcode}", package.Barcode);
                    _notificationService.ShowWarning("非履约日包裹");
                    await _warningLightService.TurnOffGreenLightAsync();
                    await Task.Delay(100); // 短暂延时确保绿灯完全关闭
                    await _warningLightService.ShowRedLightAsync();
                    package.SetError($"非履约日包裹：{result.Message}");
                    break;

                case 1004:
                    // 重量异常
                    Log.Warning("重量异常：{Barcode}, {Message}", package.Barcode, result.Message);
                    _notificationService.ShowWarning("重量异常");
                    await _warningLightService.TurnOffGreenLightAsync();
                    await Task.Delay(100); // 短暂延时确保绿灯完全关闭
                    await _warningLightService.ShowRedLightAsync();
                    package.SetError($"重量异常：{result.Message}");
                    break;

                case 400:
                    // 客户端请求错误
                    Log.Warning("客户端请求错误：{Message}", result.Message);
                    _notificationService.ShowError("请求错误");
                    await _warningLightService.TurnOffGreenLightAsync();
                    await Task.Delay(100); // 短暂延时确保绿灯完全关闭
                    await _warningLightService.ShowRedLightAsync();
                    package.SetError($"请求错误：{result.Message}");
                    break;

                case 500:
                    // 服务端异常
                    Log.Error("服务端异常：{Message}", result.Message);
                    _notificationService.ShowError("服务异常");
                    await _warningLightService.TurnOffGreenLightAsync();
                    await Task.Delay(100); // 短暂延时确保绿灯完全关闭
                    await _warningLightService.ShowRedLightAsync();
                    package.SetError($"服务异常：{result.Message}");
                    break;

                default:
                    // 未知错误
                    Log.Error("未知错误：Code={Code}, Message={Message}", result.Code, result.Message);
                    _notificationService.ShowError("未知错误");
                    await _warningLightService.TurnOffGreenLightAsync();
                    await Task.Delay(100); // 短暂延时确保绿灯完全关闭
                    await _warningLightService.ShowRedLightAsync();
                    package.SetError($"未知错误[{result.Code}]：{result.Message}");
                    break;
            }

            return result;
        }
        catch (Exception ex)
        {
            // 记录更详细的异常信息
            switch (ex)
            {
                case HttpRequestException httpEx:
                    Log.Error(ex, "DWS服务HTTP请求异常: {StatusCode}, {Message}",
                        httpEx.StatusCode, ex.Message);
                    package.SetError($"网络请求异常：{httpEx.Message}");
                    break;
                case TaskCanceledException:
                    Log.Error(ex, "DWS服务请求超时");
                    package.SetError("请求超时，请检查网络连接");
                    break;
                case JsonException jsonEx:
                    Log.Error(ex, "DWS服务响应解析异常: {Message}", jsonEx.Message);
                    package.SetError("响应数据解析失败");
                    break;
                default:
                    Log.Error(ex, "DWS服务请求异常: {Type}, {Message}", ex.GetType().Name, ex.Message);
                    package.SetError($"系统异常：{ex.Message}");
                    break;
            }

            return new DwsResponse { Code = 500, Message = ex.Message };
        }
    }

    /// <summary>
    ///     处理配置变更事件
    /// </summary>
    private void OnConfigurationChanged(UploadConfiguration newConfig)
    {
        Log.Information("DWS服务配置已更新");
        _currentConfig = newConfig;
    }

    private async Task CheckNetworkStatusAsync()
    {
        try
        {
            var baseAddress = _currentConfig.Environment == UploadEnvironment.Production
                ? "klwms.meituan.com"
                : "klvwms.meituan.com";

            using var ping = new Ping();
            var reply = await ping.SendPingAsync(baseAddress);
            var isAvailable = reply.Status == IPStatus.Success;

            if (_isNetworkAvailable != isAvailable)
            {
                _isNetworkAvailable = isAvailable;
                if (isAvailable)
                {
                    Log.Information("网络已恢复连接");
                    _notificationService.ShowSuccess("网络已恢复");
                }
                else
                {
                    Log.Warning("网络连接已断开");
                    _notificationService.ShowWarning("网络断开");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "检查网络状态时发生错误");
        }
    }

    private void CheckNetworkStatus(object? state)
    {
        _ = CheckNetworkStatusAsync();
    }

    private async Task RetryOfflinePackagesAsync()
    {
        if (!_isNetworkAvailable)
        {
            Log.Information("网络未恢复，跳过离线包裹重试");
            return;
        }

        try
        {
            while (true)
            {
                var offlinePackages = await _offlinePackageService.GetOfflinePackagesAsync();
                if (offlinePackages.Count == 0)
                {
                    Log.Information("没有待上传的离线包裹");
                    break;
                }

                Log.Information("发现 {Count} 个离线包裹待上传", offlinePackages.Count);
                var successCount = 0;
                var failCount = 0;
                var errorMessages = new List<string>();

                foreach (var package in offlinePackages)
                {
                    try
                    {
                        Log.Information("开始重试上传包裹：{Barcode}", package.Barcode);
                        var result = await ReportPackageAsync(package);

                        if (result.Code != 200)
                        {
                            failCount++;
                            var errorMessage = $"包裹 {package.Barcode} 上传失败：{result.Message}";
                            errorMessages.Add(errorMessage);
                            Log.Warning(errorMessage);
                            continue;
                        }

                        await _offlinePackageService.DeleteOfflinePackageAsync(package.Barcode);
                        successCount++;
                        Log.Information("离线包裹上传成功并已从数据库中删除：{Barcode}", package.Barcode);
                    }
                    catch (Exception ex)
                    {
                        failCount++;
                        var errorMessage = $"包裹 {package.Barcode} 上传异常：{ex.Message}";
                        errorMessages.Add(errorMessage);
                        Log.Error(ex, errorMessage);
                    }
                }

                // 显示上传结果通知
                if (successCount > 0)
                {
                    _notificationService.ShowSuccess($"成功上传 {successCount} 个包裹");
                }

                if (failCount > 0)
                {
                    var message = $"有 {failCount} 个包裹上传失败";
                    if (errorMessages.Count > 0)
                    {
                        message += $"\n{string.Join("\n", errorMessages.Take(3))}";
                        if (errorMessages.Count > 3)
                        {
                            message += $"\n...等 {errorMessages.Count} 个错误";
                        }
                    }
                    _notificationService.ShowWarning(message);
                }

                // 如果所有包裹都上传失败，等待一段时间后重试
                if (failCount != offlinePackages.Count) continue;
                Log.Warning("所有包裹上传失败，等待30秒后重试");
                await Task.Delay(TimeSpan.FromSeconds(30));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理离线包裹时发生错误");
            _notificationService.ShowError("处理离线包裹时发生错误");
        }
    }

    private void RetryOfflinePackages(object? state)
    {
        _ = RetryOfflinePackagesAsync();
    }

    /// <summary>
    ///     释放资源
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            // 释放定时器
            _networkCheckTimer.Dispose();
            _offlinePackageRetryTimer.Dispose();
        }

        _disposed = true;
    }
}