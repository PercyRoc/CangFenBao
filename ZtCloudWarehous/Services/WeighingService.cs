using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Web;
using Common.Services.Settings;
using Serilog;
using ZtCloudWarehous.Models;
using ZtCloudWarehous.Utils;
using ZtCloudWarehous.ViewModels.Settings;
using System.Diagnostics;

namespace ZtCloudWarehous.Services;

/// <summary>
///     称重服务实现
/// </summary>
internal class WeighingService(ISettingsService settingsService) : IWeighingService, IDisposable
{
    private readonly HttpClient _httpClient = new();
    private const string UatBaseUrl = "https://scm-gateway-uat.ztocwst.com/edi/service/inbound/bz";
    private const string ProdBaseUrl = "https://scm-openapi.ztocwst.com/edi/service/inbound/bz";

    /// <inheritdoc />
    public async Task<WeighingResponse> SendWeightDataAsync(WeighingRequest request)
    {
        var stopwatch = new Stopwatch();
        try
        {
            var settings = settingsService.LoadSettings<WeighingSettings>();
            
            var baseUrl = settings.IsProduction ? ProdBaseUrl : UatBaseUrl;

            // 设置公共参数
            request.Api = "shanhaitong.wms.dws.weight";
            request.CustomerId = settings.CompanyCode;
            request.AppKey = settings.AppKey;

            // 设置业务参数
            request.TenantId = settings.TenantId;
            request.WarehouseCode = settings.WarehouseCode;
            request.UserRealName = settings.UserRealName;

            // 获取公共参数和业务参数
            var commonParams = SignatureHelper.GetCommonParameters(request);
            var businessParams = SignatureHelper.GetBusinessParameters(request);

            // 计算签名
            request.Sign = SignatureHelper.CalculateSignature(commonParams, businessParams, settings.Secret);

            // 构建 URL 查询参数 (公共参数 + 签名)
            var queryParameters = new Dictionary<string, string>
            {
                { "api", request.Api },
                { "customerId", request.CustomerId },
                { "appkey", request.AppKey },
                { "sign", request.Sign }
            };
            
            // 对 Query 参数进行 UTF-8 URL 编码
            var encodedQueryParameters = queryParameters.ToDictionary(
                kvp => HttpUtility.UrlEncode(kvp.Key, Encoding.UTF8),
                kvp => HttpUtility.UrlEncode(kvp.Value, Encoding.UTF8)
            );
            var queryString = string.Join("&", encodedQueryParameters.Select(kvp => $"{kvp.Key}={kvp.Value}"));
            var requestUrl = $"{baseUrl}?{queryString}";

            // 构建请求体 (业务参数 JSON)
            // ** 在这里重新构建业务 JSON，确保与签名使用的 JSON 一致 **
            var businessParamNamesInOrder = new[]
            {
                "tenantId", "warehouseCode", "waybillCode", "packagingMaterialCode",
                "actualVolume", "actualWeight", "weighingEquipment", "userId", "userRealName"
            };
            var businessJsonBuilder = new StringBuilder("{");
            var isFirst = true;
            foreach (var key in businessParamNamesInOrder)
            {
                if (!businessParams.TryGetValue(key, out var value)) continue; // 使用从 SignatureHelper.GetBusinessParameters 获取的 businessParams
                if (!isFirst) { businessJsonBuilder.Append(','); }
                businessJsonBuilder.Append('"').Append(key).Append("\":");
                switch (value)
                {
                    case string strValue:
                        businessJsonBuilder.Append('"').Append(strValue.Replace("\"", "\\\"")).Append('"');
                        break;
                    case bool boolValue:
                        businessJsonBuilder.Append(boolValue ? "true" : "false");
                        break;
                    case null:
                        businessJsonBuilder.Append("\"\""); // 空字符串
                        break;
                    default:
                        businessJsonBuilder.Append(value); // 数字等
                        break;
                }
                isFirst = false;
            }
            businessJsonBuilder.Append('}');
            var businessParamsJsonForBody = businessJsonBuilder.ToString();
            // ** 结束重新构建业务 JSON **

            var requestBody = new StringContent(businessParamsJsonForBody, Encoding.UTF8, System.Net.Mime.MediaTypeNames.Application.Json); // 使用 MediaTypeNames 修正类型
            // 记录请求 URL 和 Body
            Log.Debug("发送称重请求 URL: {RequestUrl}", requestUrl);
            Log.Debug("发送称重请求 Body: {RequestBody}", businessParamsJsonForBody);

            // 发送请求 (包含 Body)
            stopwatch.Start();
            var response = await _httpClient.PostAsync(requestUrl, requestBody);
            stopwatch.Stop();
            Log.Information("称重请求 HttpClient.PostAsync 耗时: {ElapsedMilliseconds}ms for {Barcode}",
                stopwatch.ElapsedMilliseconds, request.WaybillCode);

            // 读取响应内容
            var responseContent = await response.Content.ReadAsStringAsync();
            Log.Debug("收到服务器响应: {Response}", responseContent);

            if (!response.IsSuccessStatusCode)
            {
                Log.Error("服务器返回错误状态码: {StatusCode}, 响应内容: {Response}", 
                    response.StatusCode, responseContent);
                throw new HttpRequestException($"服务器返回错误状态码: {response.StatusCode}");
            }

            try
            {
                // 尝试解析为错误消息
                if (!responseContent.StartsWith('{'))
                {
                    throw new Exception(responseContent);
                }

                var result = await response.Content.ReadFromJsonAsync<WeighingResponse>();
                if (result == null)
                {
                    Log.Error("服务器返回空响应");
                    throw new Exception("服务器返回空响应");
                }

                if (!result.Success)
                {
                    Log.Warning("称重请求失败: Code={Code}, Message={Message}", result.Code, result.Message);
                }

                return result;
            }
            catch (JsonException ex)
            {
                Log.Error(ex, "解析服务器响应失败: {Response}", responseContent);
                throw new Exception(responseContent);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "发送称重数据时发生错误，耗时: {ElapsedMilliseconds}ms", stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}