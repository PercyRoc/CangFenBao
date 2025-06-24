using Common.Services.Settings;
using Serilog;
using Sunnen.Models;
using Sunnen.Models.Settings;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Sunnen.Services;

internal class SangNengService(ISettingsService settingsService) : ISangNengService
{
    private readonly HttpClient _httpClient = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<SangNengWeightResponse> SendWeightDataAsync(SangNengWeightRequest request)
    {
        try
        {
            // 在每次请求时获取最新的设置
            var settings = settingsService.LoadSettings<SangNengSettings>();
            Log.Information("发送前加载桑能配置: Username={Username}, Password={Password}, Sign={Sign}", 
                settings.Username, 
                settings.Password, 
                settings.Sign);

            // 更新认证头 (如果需要每次更新的话)
            var authToken = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{settings.Password}"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);

            const string url = "https://cbeltapi3.247express.vn/api/DWSController/ProvidedByDWS";

            // 修改时间戳为本地时间
            if (!string.IsNullOrEmpty(request.Timestamp))
            {
                var utcTime = DateTime.Parse(request.Timestamp).ToUniversalTime();
                var localTime = TimeZoneInfo.ConvertTimeFromUtc(utcTime, TimeZoneInfo.Local);
                request.Timestamp = localTime.ToString("yyyy-MM-dd HH:mm:ss");
            }

            // 设置 Sign 字段
            request.Sign = settings.Sign;

            var json = JsonSerializer.Serialize(request, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(url, content);

            var responseContent = await response.Content.ReadAsStringAsync();
            Log.Information("收到桑能服务器响应: {Response}", responseContent);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("桑能服务器返回错误状态码: {StatusCode}", response.StatusCode);
                return new SangNengWeightResponse
                {
                    Code = (int)response.StatusCode,
                    Message = $"HTTP错误: {response.StatusCode}"
                };
            }

            var result = JsonSerializer.Deserialize<SangNengWeightResponse>(responseContent, _jsonOptions);
            if (result != null) return result;

            Log.Error("无法解析桑能服务器响应");
            return new SangNengWeightResponse
            {
                Code = -1,
                Message = "无法解析服务器响应"
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "发送数据到桑能服务器时发生错误");
            return new SangNengWeightResponse
            {
                Code = -1,
                Message = $"发送失败: {ex.Message}"
            };
        }
    }
}