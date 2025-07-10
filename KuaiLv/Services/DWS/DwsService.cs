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
public class DwsService : IDwsService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Timer _networkCheckTimer;
    private readonly INotificationService _notificationService;
    private readonly Timer _offlinePackageRetryTimer;
    private readonly OfflinePackageService _offlinePackageService;
    private readonly ISettingsService _settingsService;
    private readonly IWarningLightService _warningLightService;
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
            // 如果是"noread"条码，不保存到离线存储
            if (string.IsNullOrEmpty(package.Barcode) || package.Barcode.Equals("noread", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning("网络未连接，但条码为noread，不保存到离线存储：{Barcode}", package.Barcode);
                package.SetStatus(PackageStatus.Error, "网络未连接，noread包裹不保存");
                return new DwsResponse
                {
                    Success = false,
                    Code = "NETWORK_OFFLINE",
                    Message = "网络未连接，noread包裹不保存到离线存储"
                };
            }
            
            Log.Warning("网络未连接，保存包裹到离线存储：{Barcode}", package.Barcode);
            await _offlinePackageService.SaveOfflinePackageAsync(package);
            package.SetStatus(PackageStatus.Error, "网络未连接，已保存到离线存储");
            return new DwsResponse
            {
                Success = false,
                Code = "NETWORK_OFFLINE",
                Message = "网络未连接，包裹已保存到离线存储"
            };
        }

            // 每次都加载最新的配置
            var uploadConfig = _settingsService.LoadSettings<UploadConfiguration>();
            var appSettings = _settingsService.LoadSettings<AppSettings>();

            // 获取当前操作模式
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
            var secret = uploadConfig.Secret;
            var signature = Convert.ToBase64String(new HMACSHA256(Encoding.UTF8.GetBytes(secret))
                .ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

            // 设置基地址 - 每次都从最新配置读取
            var baseAddress = uploadConfig.Environment == UploadEnvironment.Production
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

            DwsResponse? result;
            try
            {
                result = JsonSerializer.Deserialize<DwsResponse>(responseContent);
            }
            catch (JsonException jsonEx)
            {
                Log.Error(jsonEx, "DWS服务响应解析异常: {ResponseContent}", responseContent);
                _notificationService.ShowError("DWS服务响应解析失败");
                package.SetStatus(PackageStatus.Error, "服务响应解析失败");
                return new DwsResponse
                {
                    Success = false,
                    Message = "服务器响应解析失败",
                    Code = "PARSE_ERROR"
                };
            }

            if (result == null)
            {
                Log.Error("DWS服务响应解析为null: {ResponseContent}", responseContent);
                _notificationService.ShowError("DWS服务异常");
                package.SetStatus(PackageStatus.Error, "服务响应解析失败");
                return new DwsResponse
                {
                    Success = false,
                    Message = "服务器响应解析失败",
                    Code = "NULL_RESPONSE"
                };
            }

            // 首先检查是否成功
            if (result.IsSuccess)
            {
                // 原始逻辑中 Code=200 且 Message 为空/非空对应不同情况
                // 现在用 IsSuccess 判断成功，用 Message 判断补充信息
                var successMessage = string.IsNullOrEmpty(result.Message) ? "上报成功：多退少补品" : result.Message;
                Log.Information("包裹上报成功：{Barcode}, Message: {Message}", package.Barcode, successMessage);
                _notificationService.ShowSuccess("包裹上报成功");
                await _warningLightService.ShowGreenLightAsync();
                package.SetStatus(PackageStatus.Success, successMessage);
                return result;
            }

            // --- 处理失败情况，优先根据 ResponseCodeValue --- 
            string? errorMessage;
            const PackageStatus errorStatus = PackageStatus.Error; // 默认错误状态

            switch (result.ResponseCodeValue)
            {
                case 1003: // 包裹号无效
                    errorMessage = result.Message;
                    Log.Warning("{ErrorMessage} Barcode: {Barcode}", errorMessage, package.Barcode);
                    _notificationService.ShowWarning("包裹号无效");
                    break;
                case 1005: // 非履约日包裹
                    errorMessage = result.Message;
                    Log.Warning("{ErrorMessage} Barcode: {Barcode}", errorMessage, package.Barcode);
                    _notificationService.ShowWarning("非履约日包裹");
                    break;
                case 1004: // 重量异常
                    errorMessage = result.Message;
                    Log.Warning("{ErrorMessage} Barcode: {Barcode}", errorMessage, package.Barcode);
                    _notificationService.ShowWarning("重量异常");
                    break;
                // 可以根据需要添加更多 ResponseCodeValue 的 case

                default:
                    switch (result.Code)
                    {
                        // 如果没有匹配的 ResponseCodeValue，则根据 Code (可能是原始的 int 或新的 string)
                        case int intCode:
                            // 尝试处理原始的 int Code
                            errorMessage = result.Message;
                            switch (intCode)
                            {
                                case 400:
                                    Log.Warning("{ErrorMessage} Barcode: {Barcode}", errorMessage, package.Barcode);
                                    _notificationService.ShowError("请求错误");
                                    break;
                                case 500:
                                    Log.Error("{ErrorMessage} Barcode: {Barcode}", errorMessage, package.Barcode);
                                    _notificationService.ShowError("服务异常");
                                    break;
                                default:
                                    Log.Error("{ErrorMessage} Barcode: {Barcode}", errorMessage, package.Barcode);
                                    _notificationService.ShowError("未知错误");
                                    break;
                            }

                            break;
                        case string strCode:
                            // 处理新的 string Code
                            errorMessage = $"服务错误[{strCode}]：{result.Message}";
                            Log.Error("{ErrorMessage} Barcode: {Barcode}", errorMessage, package.Barcode);
                            _notificationService.ShowError("服务错误");
                            break;
                        default:
                            // Code 是 null 或其他类型
                            errorMessage = $"处理失败：{result.Message}";
                            Log.Error("{ErrorMessage} Barcode: {Barcode}", errorMessage, package.Barcode);
                            _notificationService.ShowError("处理失败");
                            break;
                    }

                    break;
            }

            package.SetStatus(errorStatus, errorMessage);

            // 统一处理失败时的亮灯逻辑
            await _warningLightService.TurnOffGreenLightAsync();
            await Task.Delay(100); // 短暂延时确保绿灯完全关闭
            await _warningLightService.ShowRedLightAsync();

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
                    package.SetStatus(PackageStatus.Error, $"网络请求异常：{httpEx.Message}");
                    // 如果不是"noread"条码，才保存到离线存储
                    if (!string.IsNullOrEmpty(package.Barcode) && !package.Barcode.Equals("noread", StringComparison.OrdinalIgnoreCase))
                    {
                        await _offlinePackageService.SaveOfflinePackageAsync(package);
                    }
                    else
                    {
                        Log.Warning("HTTP请求异常，但条码为noread，不保存到离线存储：{Barcode}", package.Barcode);
                    }
                    break;
                case TaskCanceledException:
                    Log.Error(ex, "DWS服务请求超时");
                    package.SetStatus(PackageStatus.Timeout, "请求超时，请检查网络连接");
                    // 如果不是"noread"条码，才保存到离线存储
                    if (!string.IsNullOrEmpty(package.Barcode) && !package.Barcode.Equals("noread", StringComparison.OrdinalIgnoreCase))
                    {
                        await _offlinePackageService.SaveOfflinePackageAsync(package);
                    }
                    else
                    {
                        Log.Warning("请求超时，但条码为noread，不保存到离线存储：{Barcode}", package.Barcode);
                    }
                    break;
                case JsonException jsonEx:
                    Log.Error(ex, "DWS服务响应解析异常: {Message}", jsonEx.Message);
                    package.SetStatus(PackageStatus.Error, "响应数据解析失败");
                    break;
                default:
                    Log.Error(ex, "DWS服务请求异常: {Type}, {Message}", ex.GetType().Name, ex.Message);
                    package.SetStatus(PackageStatus.Error, $"系统异常：{ex.Message}");
                    break;
            }

            return new DwsResponse
            {
                Code = 500,
                Message = ex.Message
            };
        }
    }

    /// <summary>
    ///     重试上报包裹信息（专门用于离线包裹重试，避免重复保存）
    /// </summary>
    private async Task<DwsResponse> RetryReportPackageAsync(PackageInfo package)
    {
        try
        {
            // 每次都加载最新的配置
            var uploadConfig = _settingsService.LoadSettings<UploadConfiguration>();
            var appSettings = _settingsService.LoadSettings<AppSettings>();

            // 获取当前操作模式
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
            var secret = uploadConfig.Secret;
            var signature = Convert.ToBase64String(new HMACSHA256(Encoding.UTF8.GetBytes(secret))
                .ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

            // 设置基地址 - 每次都从最新配置读取
            var baseAddress = uploadConfig.Environment == UploadEnvironment.Production
                ? "https://klwms.meituan.com"
                : "https://klvwms.meituan.com";

            // 记录请求前的信息
            Log.Information("DWS重试请求参数: BaseUrl={BaseUrl}, Path={Path}, Method={Method}",
                baseAddress, path, "POST");
            Log.Information("DWS重试请求头: Accept={Accept}, App={App}, Timestamp={Timestamp}, MD5={MD5}",
                "application/json", "kl_dws_weighing", timestamp, contentMd5);
            Log.Information("DWS重试请求体: {RequestBody}", jsonContent);

            // 使用完整URL
            var fullUrl = new Uri($"{baseAddress}{path}");
            Log.Information("DWS重试完整请求URL: {FullUrl}", fullUrl);

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, fullUrl)
            {
                Content = content
            };

            // 设置请求头部
            requestMessage.Headers.Add("Accept", "application/json");
            requestMessage.Headers.Add("S-Ca-App", "kl_dws_weighing");
            requestMessage.Headers.Add("S-Ca-Timestamp", timestamp);
            requestMessage.Headers.Add("S-Ca-Signature", signature);

            // 发送请求
            Log.Information("开始发送DWS重试请求...");
            var response = await _httpClient.SendAsync(requestMessage);
            Log.Information("DWS重试响应状态码: {StatusCode}", response.StatusCode);

            var responseContent = await response.Content.ReadAsStringAsync();
            Log.Information("DWS重试响应内容: {ResponseContent}", responseContent);

            DwsResponse? result;
            try
            {
                result = JsonSerializer.Deserialize<DwsResponse>(responseContent);
            }
            catch (JsonException jsonEx)
            {
                Log.Error(jsonEx, "DWS重试服务响应解析异常: {ResponseContent}", responseContent);
                package.SetStatus(PackageStatus.Error, "服务响应解析失败");
                return new DwsResponse
                {
                    Success = false,
                    Message = "服务器响应解析失败",
                    Code = "PARSE_ERROR"
                };
            }

            if (result == null)
            {
                Log.Error("DWS重试服务响应解析为null: {ResponseContent}", responseContent);
                package.SetStatus(PackageStatus.Error, "服务响应解析失败");
                return new DwsResponse
                {
                    Success = false,
                    Message = "服务器响应解析失败",
                    Code = "NULL_RESPONSE"
                };
            }

            // 检查是否成功
            if (result.IsSuccess)
            {
                var successMessage = string.IsNullOrEmpty(result.Message) ? "重试上报成功：多退少补品" : result.Message;
                Log.Information("包裹重试上报成功：{Barcode}, Message: {Message}", package.Barcode, successMessage);
                package.SetStatus(PackageStatus.Success, successMessage);
                return result;
            }

            // 处理失败情况
            string? errorMessage;
            const PackageStatus errorStatus = PackageStatus.Error;

            switch (result.ResponseCodeValue)
            {
                case 1003:
                case 1005:
                case 1004:
                    errorMessage = result.Message;
                    Log.Warning("重试上报 - {ErrorMessage} Barcode: {Barcode}", errorMessage, package.Barcode);
                    break;
                default:
                    errorMessage = result.Code switch
                    {
                        int => result.Message,
                        string strCode => $"服务错误[{strCode}]：{result.Message}",
                        _ => $"处理失败：{result.Message}"
                    };
                    Log.Error("重试上报 - {ErrorMessage} Barcode: {Barcode}", errorMessage, package.Barcode);
                    break;
            }

            package.SetStatus(errorStatus, errorMessage);
            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DWS重试服务请求异常: {Type}, {Message}", ex.GetType().Name, ex.Message);
            package.SetStatus(PackageStatus.Error, $"重试异常：{ex.Message}");
            return new DwsResponse
            {
                Code = 500,
                Message = ex.Message
            };
        }
    }

    private async Task CheckNetworkStatusAsync()
    {
        try
        {
            // 每次检查都加载最新的配置
            var uploadConfig = _settingsService.LoadSettings<UploadConfiguration>();
            var baseAddress = uploadConfig.Environment == UploadEnvironment.Production
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
        catch (PingException pingEx) // 特别处理 Ping 异常
        {
            // 记录更具体的 Ping 错误信息，包括内部 SocketException
            Log.Warning(pingEx, "检查网络状态失败 (PingException): {Message}. 内层错误: {InnerMessage}",
                pingEx.Message, pingEx.InnerException?.Message ?? "无内层错误");

            if (_isNetworkAvailable) // 仅当状态改变时更新
            {
                _isNetworkAvailable = false;
                Log.Warning("网络连接已断开 (无法 Ping 通主机)");
                _notificationService.ShowWarning("网络断开 (无法访问服务)");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "检查网络状态时发生未知错误");
            // 对于其他未知错误，也可以考虑将网络标记为不可用，或者保持当前状态并继续尝试
            // 这里暂时保持不变，只记录错误，避免因瞬时问题误判网络状态
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
                // 首先检查当前网络状态，如果不可用，则不尝试上传，直接跳过
                if (!_isNetworkAvailable)
                {
                    Log.Information("网络未连接，暂时跳过离线包裹上传");
                    break; // 跳出 while 循环，等待下一个定时器周期
                }

                var offlinePackages = await _offlinePackageService.GetOfflinePackagesAsync();
                if (offlinePackages.Count == 0)
                {
                    Log.Information("没有待上传的离线包裹");
                    break;
                }

                Log.Information("发现 {Count} 个离线包裹待上传", offlinePackages.Count);
                var successCount = 0;
                var failCount = 0;
                var skippedCount = 0; // 用于跟踪因网络问题临时跳过的数量
                var errorMessages = new List<string>();

                foreach (var package in offlinePackages)
                {
                    // 再次检查网络，以防在处理过程中网络状态变化
                    if (!_isNetworkAvailable)
                    {
                        Log.Warning("处理 {Barcode} 前网络断开，本次重试暂停", package.Barcode);
                        skippedCount++;
                        continue;
                    }

                    try
                    {
                        Log.Information("开始重试上传包裹：{Barcode}", package.Barcode);
                        // 调用专门的重试上报接口，避免重复保存
                        var result = await RetryReportPackageAsync(package);

                        // 无论成功还是失败，都标记为已处理，不再重试
                        await _offlinePackageService.MarkOfflinePackageAsRetryCompletedAsync(package.Barcode, package.CreateTime, result.IsSuccess);
                        
                        if (result.IsSuccess)
                        {
                            successCount++;
                            Log.Information("离线包裹重试上传成功：{Barcode}", package.Barcode);
                        }
                        else
                        {
                            failCount++;
                            var errorMessage = $"离线包裹 {package.Barcode} 重试上传失败：Code={result.Code}, Value={result.ResponseCodeValue}, Msg='{result.Message}'";
                            errorMessages.Add(errorMessage);
                            Log.Warning(errorMessage);
                        }
                    }
                    catch (Exception ex)
                    {
                        // 发生异常时也标记为已处理，不再重试
                        await _offlinePackageService.MarkOfflinePackageAsRetryCompletedAsync(package.Barcode, package.CreateTime, false);
                        failCount++;
                        var errorMessage = $"处理离线包裹 {package.Barcode} 时发生意外异常：{ex.Message}";
                        errorMessages.Add(errorMessage);
                        Log.Error(ex, errorMessage);
                    }
                }

                // 重试上传不显示通知，仅记录日志
                if (successCount > 0)
                {
                    Log.Information("重试上传成功 {SuccessCount} 个离线包裹", successCount);
                }
                if (failCount > 0)
                {
                    Log.Warning("重试上传失败 {FailCount} 个离线包裹，已标记为不再重试", failCount);
                    if (errorMessages.Count > 0)
                    {
                        Log.Warning("重试失败详情: {ErrorDetails}", string.Join("; ", errorMessages.Take(5)));
                    }
                }
                if (skippedCount > 0)
                {
                    Log.Information("因网络断开，本次跳过 {SkippedCount} 个离线包裹的处理", skippedCount);
                }

                if (failCount + skippedCount != offlinePackages.Count || offlinePackages.Count <= 0) continue;
                Log.Warning("本次离线包裹处理中所有包裹均失败或跳过，可能网络持续不稳定，等待1分钟后重试");
                await Task.Delay(TimeSpan.FromMinutes(1));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理离线包裹时发生错误");
            // 重试过程中的错误不显示通知，仅记录日志
        }
    }

    private void RetryOfflinePackages(object? state)
    {
        _ = RetryOfflinePackagesAsync();
    }

    /// <summary>
    ///     释放资源
    /// </summary>
    protected void Dispose(bool disposing)
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