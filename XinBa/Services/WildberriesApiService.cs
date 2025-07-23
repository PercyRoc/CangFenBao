using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Common.Services.Settings;
using Serilog;
using XinBa.Models.Settings;
using XinBa.Services.Models;

namespace XinBa.Services;

public class WildberriesApiService
{
    private readonly HttpClient _httpClient;
    private readonly WildberriesApiSettings _settings;

    public WildberriesApiService(HttpClient httpClient, ISettingsService settingsService)
    {
        _httpClient = httpClient;

        _settings = settingsService.LoadSettings<WildberriesApiSettings>();

        _httpClient.BaseAddress = new Uri(_settings.BaseUrl);
        var byteArray = Encoding.ASCII.GetBytes($"{_settings.Username}:{_settings.Password}");
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));
    }

    public async Task<(bool success, string errorMessage)> SendTareAttributesAsync(TareAttributesRequest request)
    {
        try
        {
            var jsonRequest = JsonSerializer.Serialize(request);
            var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

            Log.Information("Sending TareAttributes request to Wildberries API: {RequestUrl}, Body: {RequestBody}", _settings.TareAttributesEndpoint, jsonRequest);

            var response = await _httpClient.PostAsync(_settings.TareAttributesEndpoint, content);

            // 读取响应内容
            var responseBody = await response.Content.ReadAsStringAsync();
            
            Log.Information("Wildberries API 响应: StatusCode={StatusCode}, Content={Content}", 
                response.StatusCode, responseBody);

            if (response.IsSuccessStatusCode)
            {
                // 检查响应内容是否为"1"（表示成功）
                var trimmedContent = responseBody.Trim();
                if (trimmedContent == "1")
                {
                    Log.Information("TareAttributes 请求成功: 响应内容={ResponseContent}", trimmedContent);
                    return (true, string.Empty);
                }
                else
                {
                    // HTTP状态码成功但响应内容不是"1"
                    Log.Warning("TareAttributes 请求失败 - 意外的响应内容: StatusCode={StatusCode}, Content={Content}",
                        response.StatusCode, responseBody);
                    return (false, $"API returned unexpected response: '{responseBody}' (expected '1')");
                }
            }
            Log.Error("TareAttributes request failed with status code {StatusCode}. Response Body: {ResponseBody}", response.StatusCode, responseBody);

            try
            {
                var errorResponse = JsonSerializer.Deserialize<TareAttributesErrorResponse>(responseBody);
                if (errorResponse?.Errors != null && errorResponse.Errors.Count > 0)
                {
                    var firstError = errorResponse.Errors[0];
                    return (false, $"API Error: {firstError.Message} (Code: {firstError.Error}, Detail: {firstError.Detail})");
                }
            }
            catch (JsonException ex)
            {
                Log.Error(ex, "Failed to deserialize error response from Wildberries API.");
            }
            return (false, $"API request failed: {response.StatusCode} - {responseBody}");
        }
        catch (HttpRequestException ex)
        {
            Log.Error(ex, "HTTP request error when sending TareAttributes to Wildberries API.");
            return (false, $"网络请求错误: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "An unexpected error occurred when sending TareAttributes to Wildberries API.");
            return (false, $"未知错误: {ex.Message}");
        }
    }
}