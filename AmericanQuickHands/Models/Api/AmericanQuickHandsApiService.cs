using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;
using Common.Services.Settings;
using AmericanQuickHands.Models.Settings;

namespace AmericanQuickHands.Models.Api;

/// <summary>
/// Swiftx API服务实现
/// </summary>
public class AmericanQuickHandsApiService(HttpClient httpClient, ISettingsService settingsService) : IAmericanQuickHandsApiService
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };


    /// <summary>
    /// 分拣机扫码接口
    /// </summary>
    /// <param name="request">分拣机扫码请求</param>
    /// <returns>扫码结果</returns>
    public async Task<ApiResponse<SwiftxResult>> SortingMachineScanAsync(SortingMachineScanRequest request)
    {
        try
        {
            var settings = settingsService.LoadSettings<AmericanQuickHandsApiSettings>();
        if (!settings.IsValid())
        {
            return ApiResponse<SwiftxResult>.CreateFailure("API配置无效");
        }

            // 设置分拣机编码
            request.SortingMachineCode = settings.SortingMachineCode;

            var response = await SendApiRequestAsync<SwiftxResult>("/api/v4/openapi/sortingMachineScan", request);
            
            if (response.Success)
            {
                Log.Information("分拣机扫码成功: {TrackingNumber}", request.TrackingNumber);
            }
            else
            {
                Log.Warning("分拣机扫码失败: {TrackingNumber}, 错误: {Error}", request.TrackingNumber, response.Message);
            }

            return response;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "分拣机扫码时发生错误: {TrackingNumber}", request.TrackingNumber);
            return ApiResponse<SwiftxResult>.CreateFailure($"扫码失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 测试API连接
    /// </summary>
    /// <returns>测试结果</returns>
    public async Task<ApiResponse<object>> TestConnectionAsync()
    {
        try
        {
            var settings = settingsService.LoadSettings<AmericanQuickHandsApiSettings>();
            if (!settings.IsValid())
            {
                return ApiResponse<object>.CreateFailure("API配置无效");
            }

            // 使用模拟数据测试分拣机扫码API
            var testRequest = new SortingMachineScanRequest
            {
                SortingMachineCode = settings.SortingMachineCode,
                TrackingNumber = "SWX784390000000365027",
                WeightKg = 1.5,
                LengthCm = 20.0,
                WidthCm = 15.0,
                HeightCm = 10.0
            };

            Log.Information("开始使用模拟数据测试API连接: {TrackingNumber}", testRequest.TrackingNumber);
            
            var response = await SortingMachineScanAsync(testRequest);
            
            if (response.Success)
            {
                Log.Information("API连接测试成功，模拟数据处理正常");
                return new ApiResponse<object>
                {
                    Success = true,
                    Message = $"连接测试成功！模拟包裹 {testRequest.TrackingNumber} 处理完成",
                    Data = response.Data
                };
            }
            else
            {
                Log.Warning("API连接测试失败: {Error}", response.Message);
                return ApiResponse<object>.CreateFailure($"连接测试失败: {response.Message}");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "测试API连接时发生错误");
            return ApiResponse<object>.CreateFailure($"连接测试失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 发送API请求
    /// </summary>
    /// <typeparam name="T">响应数据类型</typeparam>
    /// <param name="endpoint">API端点</param>
    /// <param name="data">请求数据</param>
    /// <returns>API响应</returns>
    private async Task<ApiResponse<T>> SendApiRequestAsync<T>(string endpoint, object data)
    {
        var requestId = Guid.NewGuid().ToString("N")[..8]; // 生成8位请求ID用于日志追踪
        
        try
        {
            Log.Information("[{RequestId}] 开始API请求: {Endpoint}", requestId, endpoint);
            
            var settings = settingsService.LoadSettings<AmericanQuickHandsApiSettings>();
            var baseUrl = settings.GetFullApiUrl();
            var url = $"{baseUrl}{endpoint}";
            
            Log.Debug("[{RequestId}] 请求URL: {Url}", requestId, url);
            
            var json = JsonSerializer.Serialize(data, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            Log.Debug("[{RequestId}] 请求体: {RequestBody}", requestId, json);
            
            // 生成认证所需的参数
            var appKey = settings.AppKey;
            var appSecret = settings.AppSecret;
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var nonce = GenerateNonce(); // 生成128位密码学安全的随机数
            var contentSha256 = ComputeSha256Hash(json);
            
            Log.Debug("[{RequestId}] 认证参数 - AppKey: {AppKey}, Timestamp: {Timestamp}, Nonce: {Nonce}, ContentSHA256: {ContentSha256}", 
                requestId, appKey, timestamp, nonce, contentSha256);
            
            // 创建签名字符串
            var uri = new Uri(url);
            var path = uri.AbsolutePath;
            var queryString = uri.Query.TrimStart('?');
            var httpMethod = "POST";
            
            var signatureString = $"{appKey}\n{timestamp}\n{nonce}\n{contentSha256}\n{httpMethod}\n{path}\n{queryString}";
            var signature = ComputeHmacSha256(signatureString, appSecret);
            
            Log.Debug("[{RequestId}] 签名字符串: {SignatureString}", requestId, signatureString);
            Log.Debug("[{RequestId}] 生成的签名: {Signature}", requestId, signature);
            
            // 添加必需的认证头
            httpClient.DefaultRequestHeaders.Clear();
            httpClient.DefaultRequestHeaders.Add("X-App-Key", appKey);
            httpClient.DefaultRequestHeaders.Add("X-Timestamp", timestamp);
            httpClient.DefaultRequestHeaders.Add("X-Nonce", nonce);
            httpClient.DefaultRequestHeaders.Add("X-Content-SHA256", contentSha256);
            httpClient.DefaultRequestHeaders.Add("X-Signature", signature);
            
            httpClient.Timeout = TimeSpan.FromSeconds(30); // 默认30秒超时
            
            Log.Information("[{RequestId}] 发送HTTP请求到: {Url}", requestId, url);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            
            var response = await httpClient.PostAsync(url, content);
            stopwatch.Stop();
            
            var responseContent = await response.Content.ReadAsStringAsync();
            
            Log.Information("[{RequestId}] 收到响应 - 状态码: {StatusCode}, 耗时: {ElapsedMs}ms, 响应长度: {ResponseLength}字符", 
                requestId, (int)response.StatusCode, stopwatch.ElapsedMilliseconds, responseContent.Length);
            
            Log.Debug("[{RequestId}] 响应内容: {ResponseContent}", requestId, responseContent);
            
            if (response.IsSuccessStatusCode)
            {
                Log.Information("[{RequestId}] API请求成功", requestId);
                
                try
                {
                    var result = JsonSerializer.Deserialize<T>(responseContent, _jsonOptions);
                    Log.Debug("[{RequestId}] 响应反序列化成功", requestId);
                    
                    return new ApiResponse<T>
                    {
                        Success = true,
                        Data = result
                    };
                }
                catch (JsonException jsonEx)
                {
                    Log.Error(jsonEx, "[{RequestId}] 响应JSON反序列化失败: {ResponseContent}", requestId, responseContent);
                    return ApiResponse<T>.CreateFailure($"响应数据格式错误: {jsonEx.Message}");
                }
            }
            else
            {
                Log.Warning("[{RequestId}] API请求失败 - HTTP {StatusCode}: {ResponseContent}", 
                    requestId, (int)response.StatusCode, responseContent);
                    
                return ApiResponse<T>.CreateFailure($"HTTP {response.StatusCode}: {responseContent}");
            }
        }
        catch (HttpRequestException httpEx)
        {
            Log.Error(httpEx, "[{RequestId}] HTTP请求异常: {Endpoint}", requestId, endpoint);
            return ApiResponse<T>.CreateFailure($"网络请求失败: {httpEx.Message}");
        }
        catch (TaskCanceledException timeoutEx)
        {
            Log.Error(timeoutEx, "[{RequestId}] 请求超时: {Endpoint}", requestId, endpoint);
            return ApiResponse<T>.CreateFailure($"请求超时: {timeoutEx.Message}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[{RequestId}] 发送API请求时发生未知错误: {Endpoint}", requestId, endpoint);
            return ApiResponse<T>.CreateFailure($"请求失败: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 生成128位密码学安全的随机数（32个字符的十六进制小写字符串）
    /// </summary>
    /// <returns>随机数字符串</returns>
    private string GenerateNonce()
    {
        using var rng = RandomNumberGenerator.Create();
        var bytes = new byte[16]; // 128位 = 16字节
        rng.GetBytes(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
    
    /// <summary>
    /// 计算字符串的SHA-256哈希值
    /// </summary>
    /// <param name="input">输入字符串</param>
    /// <returns>64个字符的十六进制小写字符串</returns>
    private string ComputeSha256Hash(string input)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(input);
        var hashBytes = sha256.ComputeHash(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
    
    /// <summary>
    /// 计算HMAC-SHA256签名
    /// </summary>
    /// <param name="message">待签名的消息</param>
    /// <param name="key">签名密钥</param>
    /// <returns>64个字符的十六进制小写字符串</returns>
    private string ComputeHmacSha256(string message, string key)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var messageBytes = Encoding.UTF8.GetBytes(message);
        var hashBytes = hmac.ComputeHash(messageBytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}