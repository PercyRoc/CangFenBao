using System.Diagnostics;
using System.Net.Http;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Web;
using Common.Services.Settings;
using Serilog;
using ZtCloudWarehous.Models;
using ZtCloudWarehous.Utils;
using ZtCloudWarehous.ViewModels.Settings;

namespace ZtCloudWarehous.Services;

/// <summary>
///     称重服务实现
/// </summary>
internal class WeighingService(ISettingsService settingsService, HttpClient httpClient) : IWeighingService
{
    private const string UatBaseUrl = "https://scm-gateway-uat.ztocwst.com/edi/service/inbound/bz";
    private const string ProdBaseUrl = "https://scm-openapi.ztocwst.com/edi/service/inbound/bz";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions CamelCaseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <inheritdoc />
    public async Task<WeighingResponse> SendWeightDataAsync(WeighingRequest request)
    {
        var stopwatch = new Stopwatch();
        long elapsedMs;
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
                {
                    "api", request.Api
                },
                {
                    "customerId", request.CustomerId
                },
                {
                    "appkey", request.AppKey
                },
                {
                    "sign", request.Sign
                }
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
                "tenantId", "warehouseCode", "waybillCode", "packagingMaterialCode", "actualVolume", "actualWeight", "weighingEquipment", "userId", "userRealName"
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

            var requestBody = new StringContent(businessParamsJsonForBody, Encoding.UTF8, MediaTypeNames.Application.Json); // 使用 MediaTypeNames 修正类型
            // 记录请求 URL 和 Body
            Log.Debug("发送称重请求 URL: {RequestUrl}", requestUrl);
            Log.Debug("发送称重请求 Body: {RequestBody}", businessParamsJsonForBody);

            // 发送请求 (包含 Body)
            stopwatch.Start();
            var response = await httpClient.PostAsync(requestUrl, requestBody);
            stopwatch.Stop();
            elapsedMs = stopwatch.ElapsedMilliseconds;

            Log.Information("称重请求完成: Barcode={Barcode}, 耗时={ElapsedMilliseconds}ms",
                request.WaybillCode, elapsedMs);

            // 记录耗时区间 (使用 Debug 级别，或根据需要调整)
            string durationCategory;
            if (elapsedMs < 1000)
            {
                durationCategory = "< 1s";
            }
            else if (elapsedMs < 2000)
            {
                durationCategory = "1s - 2s";
            }
            else
            {
                durationCategory = ">= 2s";
            }
            Log.Debug("称重请求耗时区间: Barcode={Barcode}, 区间={DurationCategory}", request.WaybillCode, durationCategory);

            // 读取响应内容
            var responseContent = await response.Content.ReadAsStringAsync();
            Log.Debug("收到服务器响应: {Response}", responseContent);

            if (!response.IsSuccessStatusCode)
            {
                Log.Error("服务器返回错误状态码: {StatusCode}, 响应内容: {Response}",
                    response.StatusCode, responseContent);
                throw new HttpRequestException($"服务器返回错误状态码: {response.StatusCode}. 内容: {responseContent}");
            }

            try
            {
                // 尝试解析为错误消息
                if (!responseContent.StartsWith('{'))
                {
                    Log.Warning("服务器响应非JSON格式: {ResponseContent}", responseContent);
                    throw new Exception($"服务器响应非JSON格式: {responseContent}");
                }

                var result = JsonSerializer.Deserialize<WeighingResponse>(responseContent, JsonOptions);

                if (result == null)
                {
                    Log.Error("服务器返回 JSON null 或无法反序列化为目标类型: {Response}", responseContent);
                    throw new Exception("服务器返回 JSON null 或无法反序列化为目标类型");
                }

                if (!result.Success)
                {
                    Log.Warning("称重请求业务失败: Code={Code}, Message={Message}, Barcode={Barcode}", result.Code, result.Message, request.WaybillCode);
                }
                else
                {
                    Log.Information("称重请求业务成功: Barcode={Barcode}", request.WaybillCode);
                }

                return result;
            }
            catch (JsonException ex)
            {
                Log.Error(ex, "解析服务器响应 JSON 失败: {Response}", responseContent);
                throw new Exception($"解析服务器响应 JSON 失败. 内容: {responseContent}", ex);
            }
        }
        catch (Exception ex)
        {
            // 在 catch 块中获取当前的 stopwatch 耗时
            stopwatch.Stop(); // 确保 stopwatch 停止计时 (可能已停止)
            elapsedMs = stopwatch.ElapsedMilliseconds; // 更新耗时变量
            // 记录错误时包含耗时
            Log.Error(ex, "发送称重数据时发生错误: Barcode={Barcode}, 耗时={ElapsedMilliseconds}ms (请求可能未完成)", request.WaybillCode, elapsedMs);
            throw; // 重新抛出异常
        }
    }

    /// <inheritdoc />
    public async Task<NewWeighingResponse> SendNewWeightDataAsync(NewWeighingRequest request)
    {
        var stopwatch = new Stopwatch();
        long elapsedMs;
        try
        {
            var settings = settingsService.LoadSettings<WeighingSettings>();

            var requestUrl = settings.NewWeighingApiUrl;

            // 构建请求体
            var requestJson = JsonSerializer.Serialize(request, CamelCaseJsonOptions);

            var requestBody = new StringContent(requestJson, Encoding.UTF8, "application/json");

            // 记录请求信息
            Log.Debug("发送新称重请求 URL: {RequestUrl}", requestUrl);
            Log.Debug("发送新称重请求 Body: {RequestBody}", requestJson);

            // 发送请求
            stopwatch.Start();
            var response = await httpClient.PostAsync(requestUrl, requestBody);
            stopwatch.Stop();
            elapsedMs = stopwatch.ElapsedMilliseconds;

            Log.Information("新称重请求完成: WaybillCode={WaybillCode}, Weight={Weight}, 耗时={ElapsedMilliseconds}ms",
                request.WaybillCode, request.Weight, elapsedMs);

            // 记录耗时区间
            string durationCategory;
            if (elapsedMs < 1000)
            {
                durationCategory = "< 1s";
            }
            else if (elapsedMs < 2000)
            {
                durationCategory = "1s - 2s";
            }
            else
            {
                durationCategory = ">= 2s";
            }
            Log.Debug("新称重请求耗时区间: WaybillCode={WaybillCode}, 区间={DurationCategory}", request.WaybillCode, durationCategory);

            // 读取响应内容
            var responseContent = await response.Content.ReadAsStringAsync();
            Log.Debug("收到新称重服务器响应: {Response}", responseContent);

            if (!response.IsSuccessStatusCode)
            {
                Log.Error("新称重服务器返回错误状态码: {StatusCode}, 响应内容: {Response}",
                    response.StatusCode, responseContent);
                throw new HttpRequestException($"新称重服务器返回错误状态码: {response.StatusCode}. 内容: {responseContent}");
            }

            try
            {
                if (!responseContent.StartsWith('{'))
                {
                    Log.Warning("新称重服务器响应非JSON格式: {ResponseContent}", responseContent);
                    throw new Exception($"新称重服务器响应非JSON格式: {responseContent}");
                }

                var result = JsonSerializer.Deserialize<NewWeighingResponse>(responseContent, JsonOptions);

                if (result == null)
                {
                    Log.Error("新称重服务器返回 JSON null 或无法反序列化为目标类型: {Response}", responseContent);
                    throw new Exception("新称重服务器返回 JSON null 或无法反序列化为目标类型");
                }

                if (!result.IsSuccess)
                {
                    Log.Warning("新称重请求业务失败: Code={Code}, Message={Message}, WaybillCode={WaybillCode}",
                        result.Code, result.Msg, request.WaybillCode);
                }
                else
                {
                    Log.Information("新称重请求业务成功: WaybillCode={WaybillCode}, CarrierCode={CarrierCode}, ProvinceName={ProvinceName}",
                        request.WaybillCode, result.Data?.CarrierCode, result.Data?.ProvinceName);
                }

                return result;
            }
            catch (JsonException ex)
            {
                Log.Error(ex, "解析新称重服务器响应 JSON 失败: {Response}", responseContent);
                throw new Exception($"解析新称重服务器响应 JSON 失败. 内容: {responseContent}", ex);
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            elapsedMs = stopwatch.ElapsedMilliseconds;
            Log.Error(ex, "发送新称重数据时发生错误: WaybillCode={WaybillCode}, 耗时={ElapsedMilliseconds}ms (请求可能未完成)",
                request.WaybillCode, elapsedMs);
            throw;
        }
    }

    /// <summary>
    ///     根据配置自动选择称重接口发送数据
    /// </summary>
    /// <param name="waybillCode">运单号</param>
    /// <param name="weight">重量</param>
    /// <param name="volume">体积（可选，仅旧接口使用）</param>
    /// <returns>是否成功</returns>
    public async Task<bool> SendWeightDataAutoAsync(string waybillCode, decimal weight, decimal? volume = null)
    {
        try
        {
            var settings = settingsService.LoadSettings<WeighingSettings>();

            if (settings.UseNewWeighingApi)
            {
                // 使用新接口
                var newRequest = new NewWeighingRequest
                {
                    WaybillCode = waybillCode,
                    Weight = weight.ToString("F2")
                };

                var newResponse = await SendNewWeightDataAsync(newRequest);
                return newResponse.IsSuccess;
            }
            // 使用旧接口
            var oldRequest = new WeighingRequest
            {
                WaybillCode = waybillCode,
                ActualWeight = weight,
                ActualVolume = volume ?? 0,
                PackagingMaterialCode = settings.PackagingMaterialCode,
                UserId = settings.UserId
            };

            var oldResponse = await SendWeightDataAsync(oldRequest);
            return oldResponse.Success;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "自动发送称重数据时发生错误: WaybillCode={WaybillCode}, Weight={Weight}", waybillCode, weight);
            return false;
        }
    }
}