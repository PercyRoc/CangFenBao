using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Serilog;
using XinBa.Services.Models;

namespace XinBa.Services;

/// <summary>
///     Tare Attributes API 服务实现
/// </summary>
public class TareAttributesApiService : ITareAttributesApiService, IDisposable
{
    // API 认证信息
    private const string ApiUsername = "simba";
    private const string ApiPassword = "1tcyoKLXH0NwVZuVuhjdl2X5BMFqp6Gnor39zaG9tKqUylFSC1ckacQxWsR10CIVspp0Cq";
    private const string ApiBaseUrl = "https://wh-skud-external.wildberries.ru";
    private const string ApiEndpoint = "/srv/measure_machine_api/api/tare_attributes_from_machine";
    private const long ApiOfficeId = 300864; // 仓库ID
    private const long ApiPlaceId = 971319209; // 机器ID

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private readonly HttpClient _httpClient;
    private bool _disposed;

    public TareAttributesApiService()
    {

        // 配置 HttpClient
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = static (_, _, _, _) => true
        };
        _httpClient = new HttpClient(handler)
        {
            // 设置基础配置
            BaseAddress = new Uri(ApiBaseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };

        // 设置 Basic 认证
        SetBasicAuthentication(ApiUsername, ApiPassword);

        // 设置请求头
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        Log.Information("Tare Attributes API 服务已初始化，Base URL: {BaseUrl}", ApiBaseUrl);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public async Task<(bool Success, string? ErrorMessage)> SubmitTareAttributesAsync(TareAttributesRequest request)
    {
        try
        {
            // 验证请求数据
            if (string.IsNullOrEmpty(request.TareSticker))
            {
                const string error = "Tare sticker cannot be empty";
                Log.Warning(error);
                return (false, error);
            }

            // 设置 API 参数
            request.OfficeId = ApiOfficeId;
            request.PlaceId = ApiPlaceId;

            // 自动计算体积
            request.CalculateVolume();

            Log.Information("开始提交 Tare Attributes: OfficeId={OfficeId}, PlaceId={PlaceId}, TareSticker={TareSticker}, Size={Length}x{Width}x{Height}mm, Weight={Weight}g, Volume={Volume}mm³",
                request.OfficeId, request.PlaceId, request.TareSticker, request.SizeAMm, request.SizeBMm, request.SizeCMm, request.WeightG, request.VolumeMm);

            // 序列化请求数据
            var jsonContent = JsonSerializer.Serialize(request, JsonOptions);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            Log.Debug("请求内容: {RequestContent}", jsonContent);

            // 发送请求
            var response = await _httpClient.PostAsync(ApiEndpoint, content);

            // 读取响应内容
            var responseContent = await response.Content.ReadAsStringAsync();
            
            Log.Information("Tare Attributes API 响应: StatusCode={StatusCode}, Content={Content}", 
                response.StatusCode, responseContent);

            // 检查响应状态和内容
            if (response.IsSuccessStatusCode)
            {
                // 204 No Content 也被认为是成功
                if (response.StatusCode == HttpStatusCode.NoContent)
                {
                    Log.Information("Tare Attributes 提交成功 (StatusCode: NoContent): TareSticker={TareSticker}", 
                        request.TareSticker);
                    return (true, null);
                }
                
                // 检查响应内容是否为"1"（表示成功）
                var trimmedContent = responseContent.Trim();
                if (trimmedContent == "1")
                {
                    Log.Information("Tare Attributes 提交成功: TareSticker={TareSticker}, 响应内容={ResponseContent}", 
                        request.TareSticker, trimmedContent);
                    return (true, null);
                }
                else
                {
                    // HTTP状态码成功但响应内容不是"1"
                    Log.Warning("Tare Attributes 提交失败 - 意外的响应内容: TareSticker={TareSticker}, StatusCode={StatusCode}, Content={Content}",
                        request.TareSticker, response.StatusCode, responseContent);
                    
                    var errorMessage = $"API returned unexpected response: '{responseContent}' (expected '1')";
                    return (false, errorMessage);
                }
            }
            else
            {
                // HTTP状态码表示失败
                Log.Error("Tare Attributes 提交失败: StatusCode={StatusCode}, Response={Response}",
                    response.StatusCode, responseContent);

                var errorMessage = await ParseErrorResponseAsync(response, responseContent);
                return (false, errorMessage);
            }
        }
        catch (HttpRequestException ex)
        {
            Log.Error(ex, "发送 Tare Attributes 请求时发生网络错误");
            var errorMessage = $"Network error: {ex.Message}";
            return (false, errorMessage);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            Log.Error(ex, "Tare Attributes 请求超时");
            const string errorMessage = "Request timeout";
            return (false, errorMessage);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "提交 Tare Attributes 时发生未知错误");
            var errorMessage = $"Unexpected error: {ex.Message}";
            return (false, errorMessage);
        }
    }

    /// <inheritdoc />
    public bool IsServiceAvailable()
    {
        return !_disposed;
    }

    /// <summary>
    ///     解析错误响应
    /// </summary>
    private static Task<string> ParseErrorResponseAsync(HttpResponseMessage response, string errorContent)
    {
        try
        {
            if (string.IsNullOrEmpty(errorContent))
            {
                return Task.FromResult($"HTTP {(int)response.StatusCode} - {response.StatusCode}");
            }

            var errorResponse = JsonSerializer.Deserialize<TareAttributesErrorResponse>(errorContent, JsonOptions);
            if (!(errorResponse?.Errors.Count > 0)) return Task.FromResult($"HTTP {(int)response.StatusCode} - {response.StatusCode}");
            var firstError = errorResponse.Errors[0];
            var message = !string.IsNullOrEmpty(firstError.Message) ? firstError.Message : firstError.Error;
            return Task.FromResult(!string.IsNullOrEmpty(message) ? message : $"HTTP {(int)response.StatusCode}");

        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "解析错误响应失败，原始内容: {ErrorContent}", errorContent);
            return Task.FromResult($"HTTP {(int)response.StatusCode} - Invalid error response format");
        }
    }

    /// <summary>
    ///     设置 HTTP Basic 认证
    /// </summary>
    private void SetBasicAuthentication(string username, string password)
    {
        var authValue = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authValue);
        Log.Debug("已设置 Basic 认证: {Username}", username);
    }

    protected void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _httpClient.Dispose();
            Log.Information("Tare Attributes API 服务已释放资源");
        }

        _disposed = true;
    }
}