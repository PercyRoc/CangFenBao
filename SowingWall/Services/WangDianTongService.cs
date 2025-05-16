using Common.Services.Settings;
using Serilog;
using SowingWall.Models.Settings;
using SowingWall.Models.WangDianTong.Api;
using SowingWall.Services.Utils;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Web;

namespace SowingWall.Services
{
    public class WangDianTongService : IWangDianTongService
    {
        private readonly HttpClient _httpClient;
        private readonly ISettingsService _settingsService;

        private const string PickOrderTaskGetMethod = "pick.order.task.get";
        private const string PickOrderStatusUpdateMethod = "pick.order.status.update";

        public WangDianTongService(HttpClient httpClient, ISettingsService settingsService)
        {
            _httpClient = httpClient;
            _settingsService = settingsService;
        }

        private WangDianTongSettings LoadWdtSettings()
        {
            return _settingsService.LoadSettings<WangDianTongSettings>();
        }

        public async Task<PickOrderTaskGetResponse?> GetPickOrderTaskAsync(PickOrderTaskGetRequest request)
        {
            var wdtSettings = LoadWdtSettings();
            if (string.IsNullOrWhiteSpace(wdtSettings.RequestUrl) || 
                string.IsNullOrWhiteSpace(wdtSettings.Sid) || 
                string.IsNullOrWhiteSpace(wdtSettings.AppKey) || 
                string.IsNullOrWhiteSpace(wdtSettings.AppSecret))
            {
                Log.Error("旺店通配置不完整 (URL, SID, AppKey, AppSecret 必须都配置)。");
                return new PickOrderTaskGetResponse { Flag = "failure", Message = "旺店通客户端配置不完整。" };
            }

            if (string.IsNullOrWhiteSpace(request.PickerShortName) || 
                request.EmptyPickerOrder < 0 || request.EmptyPickerOrder > 1 || (!request.Status.HasValue && string.IsNullOrWhiteSpace(request.PickNo)))
            {
                Log.Warning("GetPickOrderTaskAsync: 无效的必要请求参数。 PickNo/Status={PickNo}/{Status}, Picker={Picker}, EmptyPicker={Empty}", request.PickNo, request.Status, request.PickerShortName, request.EmptyPickerOrder);
                return new PickOrderTaskGetResponse { Flag = "failure", Message = "请求参数错误：必要参数缺失或无效。" };
            }

            try
            {
                var (jsonRequestBody, publicParameters) = WangDianTongSignUtil.PrepareRequestComponents(
                    request, 
                    wdtSettings.Sid, 
                    wdtSettings.AppKey, 
                    wdtSettings.AppSecret, 
                    PickOrderTaskGetMethod);

                var uriBuilder = new UriBuilder(wdtSettings.RequestUrl);
                var query = HttpUtility.ParseQueryString(uriBuilder.Query);
                foreach (var kvp in publicParameters)
                {
                    query[kvp.Key] = kvp.Value;
                }
                uriBuilder.Query = query.ToString();
                string fullRequestUrl = uriBuilder.ToString();

                var content = new StringContent(jsonRequestBody, Encoding.UTF8, "application/json");

                Log.Information("调用旺店通接口 {Method}: URL={RequestUrl}, Body={RequestBody}", 
                    PickOrderTaskGetMethod, fullRequestUrl, jsonRequestBody);

                HttpResponseMessage response = await _httpClient.PostAsync(fullRequestUrl, content);
                
                string responseBody = await response.Content.ReadAsStringAsync();
                Log.Information("旺店通接口 {Method} 响应: StatusCode={StatusCode}, Body={ResponseBody}", PickOrderTaskGetMethod, response.StatusCode, responseBody);

                if (response.IsSuccessStatusCode)
                {
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true }; 
                    var apiResponse = JsonSerializer.Deserialize<PickOrderTaskGetResponse>(responseBody, options);
                    
                    if (apiResponse == null)
                    {
                        Log.Error("反序列化旺店通响应失败: {ResponseBody}", responseBody);
                        return new PickOrderTaskGetResponse { Flag = "failure", Message = "无法解析API响应。" };
                    }
                    if (!apiResponse.IsSuccess)
                    {
                        Log.Warning("旺店通API返回错误: Flag={Flag}, Code={Code}, Message={Message}", apiResponse.Flag, apiResponse.Code, apiResponse.Message);
                    }
                    return apiResponse;
                }
                else
                {
                    Log.Error("调用旺店通接口 {Method} 失败: StatusCode={StatusCode}, Reason={ReasonPhrase}, Body={ResponseBody}", 
                        PickOrderTaskGetMethod, response.StatusCode, response.ReasonPhrase, responseBody);
                    return new PickOrderTaskGetResponse { Flag = "failure", Message = $"HTTP请求失败: {(int)response.StatusCode} {response.ReasonPhrase}" };
                }
            }
            catch (HttpRequestException hex)
            {
                Log.Error(hex, "调用旺店通接口 {Method} 时发生HttpRequestException", PickOrderTaskGetMethod);
                 return new PickOrderTaskGetResponse { Flag = "failure", Message = $"网络请求异常: {hex.Message}" };
            }
            catch (JsonException jex)
            {
                Log.Error(jex, "处理旺店通接口 {Method} 时发生JsonException", PickOrderTaskGetMethod);
                return new PickOrderTaskGetResponse { Flag = "failure", Message = $"JSON处理错误: {jex.Message}" };
            }
            catch (UriFormatException ufx)
            {
                 Log.Error(ufx, "构建旺店通请求 URL 时格式错误: BaseUrl={BaseUrl}", wdtSettings.RequestUrl);
                 return new PickOrderTaskGetResponse { Flag = "failure", Message = $"请求URL格式错误: {ufx.Message}" };
            }
            catch (Exception ex)
            {
                Log.Error(ex, "调用旺店通接口 {Method} 时发生未知错误", PickOrderTaskGetMethod);
                return new PickOrderTaskGetResponse { Flag = "failure", Message = $"发生内部错误: {ex.Message}" };
            }
        }

        public async Task<WdtApiResponseBase?> UpdatePickOrderStatusAsync(PickOrderStatusUpdateRequest request)
        {
            var wdtSettings = LoadWdtSettings();
            if (string.IsNullOrWhiteSpace(wdtSettings.RequestUrl) || 
                string.IsNullOrWhiteSpace(wdtSettings.Sid) || 
                string.IsNullOrWhiteSpace(wdtSettings.AppKey) || 
                string.IsNullOrWhiteSpace(wdtSettings.AppSecret))
            {
                Log.Error("旺店通配置不完整 (URL, SID, AppKey, AppSecret 必须都配置)。");
                return new WdtApiResponseBaseConcrete { Flag = "failure", Message = "旺店通客户端配置不完整。" };
            }

            if (string.IsNullOrWhiteSpace(request.PickNo) || 
                (request.Status != 30 && request.Status != 35 && request.Status != 40))
            {
                Log.Warning("UpdatePickOrderStatusAsync: 无效的请求参数. PickNo={PickNo}, Status={Status}", request.PickNo, request.Status);
                return new WdtApiResponseBaseConcrete { Flag = "failure", Message = "请求参数错误：分拣单号为必填，状态必须是30, 35, 或 40。" };
            }

            try
            {
                var (jsonRequestBody, publicParameters) = WangDianTongSignUtil.PrepareRequestComponents(
                    request, 
                    wdtSettings.Sid, 
                    wdtSettings.AppKey, 
                    wdtSettings.AppSecret, 
                    PickOrderStatusUpdateMethod);

                var uriBuilder = new UriBuilder(wdtSettings.RequestUrl);
                var query = HttpUtility.ParseQueryString(uriBuilder.Query);
                foreach (var kvp in publicParameters)
                {
                    query[kvp.Key] = kvp.Value;
                }
                uriBuilder.Query = query.ToString();
                string fullRequestUrl = uriBuilder.ToString();

                var content = new StringContent(jsonRequestBody, Encoding.UTF8, "application/json");

                Log.Information("调用旺店通接口 {Method}: URL={RequestUrl}, Body={RequestBody}", 
                    PickOrderStatusUpdateMethod, fullRequestUrl, jsonRequestBody);

                HttpResponseMessage response = await _httpClient.PostAsync(fullRequestUrl, content);
                
                string responseBody = await response.Content.ReadAsStringAsync();
                Log.Information("旺店通接口 {Method} 响应: StatusCode={StatusCode}, Body={ResponseBody}", PickOrderStatusUpdateMethod, response.StatusCode, responseBody);

                if (response.IsSuccessStatusCode)
                {
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var apiResponse = JsonSerializer.Deserialize<WdtApiResponseBaseConcrete>(responseBody, options); 
                    
                    if (apiResponse == null)
                    {
                        Log.Error("反序列化旺店通响应失败: {ResponseBody}", responseBody);
                        return new WdtApiResponseBaseConcrete { Flag = "failure", Message = "无法解析API响应。" };
                    }
                     if (!apiResponse.IsSuccess)
                    {
                        Log.Warning("旺店通API返回错误: Flag={Flag}, Code={Code}, Message={Message}", apiResponse.Flag, apiResponse.Code, apiResponse.Message);
                    }
                    return apiResponse;
                }
                else
                {
                    Log.Error("调用旺店通接口 {Method} 失败: StatusCode={StatusCode}, Reason={ReasonPhrase}, Body={ResponseBody}", 
                        PickOrderStatusUpdateMethod, response.StatusCode, response.ReasonPhrase, responseBody);
                    return new WdtApiResponseBaseConcrete { Flag = "failure", Message = $"HTTP请求失败: {(int)response.StatusCode} {response.ReasonPhrase}" };
                }
            }
            catch (HttpRequestException hex)
            {
                Log.Error(hex, "调用旺店通接口 {Method} 时发生HttpRequestException", PickOrderStatusUpdateMethod);
                 return new WdtApiResponseBaseConcrete { Flag = "failure", Message = $"网络请求异常: {hex.Message}" };
            }
            catch (JsonException jex)
            {
                Log.Error(jex, "处理旺店通接口 {Method} 响应时发生JsonException", PickOrderStatusUpdateMethod);
                return new WdtApiResponseBaseConcrete { Flag = "failure", Message = $"JSON处理错误: {jex.Message}" };
            }
            catch (UriFormatException ufx)
            {
                 Log.Error(ufx, "构建旺店通请求 URL 时格式错误: BaseUrl={BaseUrl}", wdtSettings.RequestUrl);
                 return new WdtApiResponseBaseConcrete { Flag = "failure", Message = $"请求URL格式错误: {ufx.Message}" };
            }
            catch (Exception ex)
            {
                Log.Error(ex, "调用旺店通接口 {Method} 时发生未知错误", PickOrderStatusUpdateMethod);
                return new WdtApiResponseBaseConcrete { Flag = "failure", Message = $"发生内部错误: {ex.Message}" };
            }
        }
    }

    internal class WdtApiResponseBaseConcrete : WdtApiResponseBase { }
} 