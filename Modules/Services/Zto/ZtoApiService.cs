using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Common.Services.Settings;
using ShanghaiModuleBelt.Models.Zto;
using ShanghaiModuleBelt.Models.Zto.Settings;
using Newtonsoft.Json;
using Serilog;

namespace ShanghaiModuleBelt.Services.Zto
{
    public class ZtoApiService : IZtoApiService
    {
        private readonly HttpClient _httpClient;
        private readonly ISettingsService _settingsService;
        private ZtoApiSettings _ztoApiSettings; // 移除 readonly 以允许重新加载

        public ZtoApiService(HttpClient httpClient, ISettingsService settingsService)
        {
            _httpClient = httpClient;
            _settingsService = settingsService;
            LoadSettingsAndConfigureHttpClient(); // 在构造函数中只加载一次设置并配置HttpClient
        }

        private void LoadSettingsAndConfigureHttpClient()
        {
            _ztoApiSettings = _settingsService.LoadSettings<ZtoApiSettings>();
            var apiUrl = _ztoApiSettings.UseTestEnvironment ? _ztoApiSettings.TestApiUrl : _ztoApiSettings.FormalApiUrl;
            _httpClient.BaseAddress = new Uri(apiUrl);

            // 清除旧的 AppKey 和 DataDigest 头，以防 HttpClient 被重用且这些头是在其他地方添加的
            _httpClient.DefaultRequestHeaders.Remove("x-appKey");
            _httpClient.DefaultRequestHeaders.Remove("x-dataDigest");
            
            _httpClient.DefaultRequestHeaders.Add("x-appKey", _ztoApiSettings.AppKey);
        }

        public async Task<CollectUploadResponse> UploadCollectTraceAsync(CollectUploadRequest request)
        {
            // 每次请求时，只更新需要变化的请求头（如 x-dataDigest），而不是重新配置整个 HttpClient
            var jsonContent = JsonConvert.SerializeObject(request);
            Log.Information("ZTO揽收上传请求数据: {JsonContent}", jsonContent);

            var dataDigest = GenerateDataDigest(jsonContent, _ztoApiSettings.Secret);
            _httpClient.DefaultRequestHeaders.Remove("x-dataDigest"); // 确保每次更新
            _httpClient.DefaultRequestHeaders.Add("x-dataDigest", dataDigest);

            try
            {
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync("", content); // 接口地址已在BaseAddress中设置

                var responseContent = await response.Content.ReadAsStringAsync();
                Log.Information("ZTO揽收上传响应内容: {ResponseContent}", responseContent);

                if (response.IsSuccessStatusCode)
                {
                    var successResponse = JsonConvert.DeserializeObject<CollectUploadResponse>(responseContent);
                    if (successResponse != null && successResponse.Status)
                    {
                        Log.Information("ZTO揽收上传成功: {Message}", successResponse.Message);
                        return successResponse;
                    }
                    else
                    {
                        var errorResponse = JsonConvert.DeserializeObject<CollectUploadErrorResponse>(responseContent);
                        Log.Warning("ZTO揽收上传业务失败: Code={Code}, Message={Message}", errorResponse?.StatusCode ?? successResponse?.Code, errorResponse?.Message ?? successResponse?.Message);
                        return new CollectUploadResponse { Status = false, Code = errorResponse?.StatusCode ?? successResponse?.Code, Message = errorResponse?.Message ?? successResponse?.Message };
                    }
                }
                else
                {
                    Log.Error("ZTO揽收上传HTTP错误: Status Code={StatusCode}, Content={Content}", response.StatusCode, responseContent);
                    return new CollectUploadResponse { Status = false, Code = response.StatusCode.ToString(), Message = $"HTTP错误: {response.ReasonPhrase}" };
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ZTO揽收上传发生异常");
                return new CollectUploadResponse { Status = false, Message = $"发生异常: {ex.Message}" };
            }
        }

        private static string GenerateDataDigest(string jsonContent, string secret)
        {
            var inputBytes = Encoding.UTF8.GetBytes(jsonContent + secret); // 签名方式为：请求体JSON + Secret
            var hashBytes = MD5.HashData(inputBytes);
            return Convert.ToBase64String(hashBytes);
        }
    }
} 