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

        public ZtoApiService(HttpClient httpClient, ISettingsService settingsService)
        {
            _httpClient = httpClient;
            _settingsService = settingsService;
        }

        public async Task<CollectUploadResponse> UploadCollectTraceAsync(CollectUploadRequest request)
        {
            // 每次请求时，重新加载最新配置，确保配置变更能实时生效
            var ztoApiSettings = _settingsService.LoadSettings<ZtoApiSettings>();
            
            var apiUrl = ztoApiSettings.UseTestEnvironment ? ztoApiSettings.TestApiUrl : ztoApiSettings.FormalApiUrl;
            // 移除 BaseAddress 设置，直接用完整 URL
            // if (_httpClient.BaseAddress == null || _httpClient.BaseAddress.ToString() != apiUrl)
            // {
            //     _httpClient.BaseAddress = new Uri(apiUrl);
            //     Log.Information("ZTO API BaseAddress 更新为: {ApiUrl}", apiUrl);
            // }

            // 移除旧的 x-appKey 和 x-dataDigest (虽然 x-dataDigest 是每次都生成的，但为了代码一致性，在这里也先移除)
            _httpClient.DefaultRequestHeaders.Remove("x-appKey");
            _httpClient.DefaultRequestHeaders.Remove("x-dataDigest");
            
            // 添加或更新 x-appKey
            _httpClient.DefaultRequestHeaders.Add("x-appKey", ztoApiSettings.AppKey);
            Log.Information("ZTO API x-appKey 设置为: {AppKey}", ztoApiSettings.AppKey);

            var jsonContent = JsonConvert.SerializeObject(request);
            Log.Information("ZTO揽收上传请求数据: {JsonContent}", jsonContent);

            if (ztoApiSettings.Secret != null)
            {
                var dataDigest = GenerateDataDigest(jsonContent, ztoApiSettings.Secret);
                // 每次请求时更新 x-dataDigest
                _httpClient.DefaultRequestHeaders.Add("x-dataDigest", dataDigest);
            }

            try
            {
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                // 直接用完整URL
                var response = await _httpClient.PostAsync(apiUrl, content);

                var responseContent = await response.Content.ReadAsStringAsync();
                Log.Information("ZTO揽收上传响应内容: {ResponseContent}", responseContent);

                if (response.IsSuccessStatusCode)
                {
                    var successResponse = JsonConvert.DeserializeObject<CollectUploadResponse>(responseContent);
                    if (successResponse is { Status: true })
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