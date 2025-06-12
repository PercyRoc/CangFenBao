using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Serilog;
using XinBa.Services.Models;
using Common.Services.Settings;
using XinBa.Models.Settings;

namespace XinBa.Services
{
    public class WildberriesApiService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly WildberriesApiSettings _settings;

        public WildberriesApiService(HttpClient httpClient, ILogger logger, ISettingsService settingsService)
        {
            _httpClient = httpClient;
            _logger = logger.ForContext<WildberriesApiService>();

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

                _logger.Information("Sending TareAttributes request to Wildberries API: {RequestUrl}, Body: {RequestBody}", _settings.TareAttributesEndpoint, jsonRequest);

                var response = await _httpClient.PostAsync(_settings.TareAttributesEndpoint, content);

                if (response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                    {
                        _logger.Information("TareAttributes request succeeded with 204 No Content.");
                        return (true, string.Empty);
                    }
                    _logger.Warning("TareAttributes request succeeded with unexpected status code: {StatusCode}", response.StatusCode);
                    return (true, string.Empty);
                }
                else
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger.Error("TareAttributes request failed with status code {StatusCode}. Response Body: {ResponseBody}", response.StatusCode, responseBody);

                    try
                    {
                        var errorResponse = JsonSerializer.Deserialize<ErrorResponse>(responseBody);
                        if (errorResponse?.errors != null && errorResponse.errors.Count > 0)
                        {
                            var firstError = errorResponse.errors[0];
                            return (false, $"API Error: {firstError.message} (Code: {firstError.error}, Detail: {firstError.detail})");
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.Error(ex, "Failed to deserialize error response from Wildberries API.");
                    }
                    return (false, $"API request failed: {response.StatusCode} - {responseBody}");
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.Error(ex, "HTTP request error when sending TareAttributes to Wildberries API.");
                return (false, $"网络请求错误: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "An unexpected error occurred when sending TareAttributes to Wildberries API.");
                return (false, $"未知错误: {ex.Message}");
            }
        }
    }
} 