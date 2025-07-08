using System.Net.Http;
using System.Text;
using Common.Services.Settings;
using FuzhouPolicyForce.Models.AnttoWeight;
using FuzhouPolicyForce.Models.Settings;
using Newtonsoft.Json;
using Serilog;

namespace FuzhouPolicyForce.Services.AnttoWeight
{
    public class AnttoWeightService : IAnttoWeightService
    {
        private const string UAT_API_URL = "https://anapi-uat.annto.com/bop/T201904230000000014/xjyc/weighing";
        private const string VER_API_URL = "https://anapi-ver.annto.com/bop/T201904230000000014/xjyc/weighing";
        private const string PROD_API_URL = "https://anapi.annto.com/bop/T201904230000000014/xjyc/weighing";

        private readonly HttpClient _httpClient;
        private readonly ISettingsService _settingsService;
        private readonly AnttoWeightSettings _anttoWeightSettings;

        public AnttoWeightService(HttpClient httpClient, ISettingsService settingsService)
        {
            _httpClient = httpClient;
            _settingsService = settingsService;
            _anttoWeightSettings = _settingsService.LoadSettings<AnttoWeightSettings>();
        }

        public async Task<AnttoWeightResponse> UploadWeightAsync(AnttoWeightRequest request)
        {
            var apiUrl = GetApiUrlByEnvironment();
            Log.Information("开始上传称重数据到安通API: {ApiUrl}", apiUrl);
            Log.Debug("请求报文: {Request}", JsonConvert.SerializeObject(request));

            try
            {
                // 构建JSON请求体
                var requestBody = new
                {
                    waybillCode = request.WaybillCode,
                    weight = request.Weight.ToString()
                };
                var jsonContent = JsonConvert.SerializeObject(requestBody);
                
                Log.Debug("GET请求URL: {ApiUrl}", apiUrl);
                Log.Debug("请求体: {RequestBody}", jsonContent);
                
                // 创建GET请求但包含JSON body
                var requestMessage = new HttpRequestMessage(HttpMethod.Get, apiUrl)
                {
                    Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
                };
                
                var response = await _httpClient.SendAsync(requestMessage);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var successResponse = JsonConvert.DeserializeObject<AnttoWeightResponse>(responseBody);
                    Log.Information("安通API称重数据上传成功: {Response}", responseBody);
                    return successResponse!;
                }
                else
                {
                    Log.Error("安通API称重数据上传失败，URL: {ApiUrl}, 状态码: {StatusCode}, 响应: {ResponseBody}", apiUrl, response.StatusCode, responseBody);
                    // 尝试反序列化失败响应以获取更多信息
                    var errorResponse = JsonConvert.DeserializeObject<AnttoWeightResponse>(responseBody);
                    return errorResponse ?? new AnttoWeightResponse { Code = response.StatusCode.ToString(), Msg = "请求失败", ErrMsg = responseBody };
                }
            }
            catch (HttpRequestException ex)
            {
                Log.Error(ex, "安通API称重数据上传时HTTP请求异常: {Message}", ex.Message);
                return new AnttoWeightResponse { Code = "NETWORK_ERROR", Msg = "网络请求失败", ErrMsg = ex.Message };
            }
            catch (JsonException ex)
            {
                Log.Error(ex, "安通API称重数据上传响应反序列化异常: {Message}", ex.Message);
                return new AnttoWeightResponse { Code = "JSON_ERROR", Msg = "响应数据解析失败", ErrMsg = ex.Message };
            }
            catch (Exception ex)
            {
                Log.Error(ex, "安通API称重数据上传时发生未知错误: {Message}", ex.Message);
                return new AnttoWeightResponse { Code = "UNKNOWN_ERROR", Msg = "未知错误", ErrMsg = ex.Message };
            }
        }

        private string GetApiUrlByEnvironment()
        {
            return _anttoWeightSettings.SelectedEnvironment switch
            {
                AnttoWeightEnvironment.UAT => UAT_API_URL,
                AnttoWeightEnvironment.VER => VER_API_URL,
                AnttoWeightEnvironment.PROD => PROD_API_URL,
                _ => UAT_API_URL // 默认返回UAT环境URL
            };
        }
    }
} 