using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Common.Services.Settings;
using SangNeng.Models;
using SangNeng.Models.Settings;
using Serilog;

namespace SangNeng.Services;

internal class SangNengService : ISangNengService
{
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;

    public SangNengService(ISettingsService settingsService)
    {
        _httpClient = new HttpClient();
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        // 配置基本认证
        var settings = settingsService.LoadSettings<SangNengSettings>();
        Log.Information("加载桑能配置: Username={Username}, Password={Password}", settings.Username, settings.Password);
        
        var authToken = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{settings.Password}"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);
    }

    public async Task<SangNengWeightResponse> SendWeightDataAsync(SangNengWeightRequest request)
    {
        try
        {
            const string url = "http://247tech.xyz:12009/api/DWSController/ProvidedByDWS";

            // 修改时间戳为本地时间
            if (!string.IsNullOrEmpty(request.Timestamp))
            {
                var utcTime = DateTime.Parse(request.Timestamp).ToUniversalTime();
                var localTime = TimeZoneInfo.ConvertTimeFromUtc(utcTime, TimeZoneInfo.Local);
                request.Timestamp = localTime.ToString("yyyy-MM-dd HH:mm:ss");
            }

            var json = JsonSerializer.Serialize(request, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(url, content);

            var responseContent = await response.Content.ReadAsStringAsync();
            Log.Debug("收到桑能服务器响应: {Response}", responseContent);

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