using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using Common.Services.Settings;
using FuzhouPolicyForce.Models;
using Serilog;

namespace FuzhouPolicyForce.WangDianTong;

/// <summary>
///     旺店通API服务实现V2 (符合新文档)
/// </summary>
public class WangDianTongApiServiceImplV2(HttpClient httpClient, ISettingsService settingsService)
    : IWangDianTongApiServiceV2
{
    // Cache JsonSerializerOptions for performance
    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull // 排除null值
    };

    /// <summary>
    ///     重量回传接口，符合旺店通新文档
    /// </summary>
    /// <param name="request">重量回传请求参数</param>
    /// <returns>重量回传响应结果</returns>
    public async Task<WeightPushResponseV2> PushWeightAsync(WeightPushRequestV2 request)
    {
        string? apiUrl = null; // 用于日志记录
        string? businessJsonBody = null; // 用于日志记录

        try
        {
            // 获取旺店通配置
            var settings = settingsService.LoadSettings<WangDianTongSettings>();

            // 构建公共参数字典 (不含sign)
            var publicParameters = new Dictionary<string, string>
            {
                {
                    "sid", settings.SellerAccount
                },
                {
                    "appkey", settings.ApiAccount
                },
                {
                    "timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()
                },
                {
                    "method", "trade.weight"
                }, // *** 注意：这个值需要根据实际文档确认！***
                {
                    "sign_method", "md5"
                },
                {
                    "format", "json"
                }
            };

            // 构建业务参数对象 (只包含非空和必要的业务参数，将null字段排除以减少签名计算的复杂性)
            // 直接使用 request 对象作为业务参数，System.Text.Json 会处理 null 和默认值
            // 如果API要求严格排除null，可能需要手动构建一个匿名对象或字典
            var businessObject = request;

            // 检查二选一必填 (针对原始请求对象进行检查)
            if (string.IsNullOrEmpty(businessObject.SrcOrderNo) && string.IsNullOrEmpty(businessObject.LogisticsNo))
                throw new ArgumentException("仓储单号和物流单号二选一必填。");

            // 序列化业务参数为 JSON 字符串
            // 使用缓存的 options 实例
            businessJsonBody = JsonSerializer.Serialize(businessObject, JsonSerializerOptions);


            // 计算签名
            var sign = CalculateSign(publicParameters, businessJsonBody, settings.ApiSecret);

            // 将签名添加到公共参数中，用于构建URL查询字符串
            publicParameters.Add("sign", sign);

            // 构建请求URL (基础URL + 公共参数作为查询字符串)
            // 使用 UriBuilder 可以更安全地构建包含查询参数的URL
            var uriBuilder = new UriBuilder($"{settings.GetApiBaseUrl()}open_api/service.php");
            var query = HttpUtility.ParseQueryString(uriBuilder.Query); // 解析现有查询参数（可能没有）

            foreach (var param in publicParameters) query[param.Key] = param.Value;
            uriBuilder.Query = query.ToString(); // 设置新的查询字符串

            apiUrl = uriBuilder.ToString();

            // 记录请求详情 (URL包含公共参数和签名，请求体是业务JSON)
            Log.Information("旺店通重量回传请求V2: URL={Url}, 业务请求体={BusinessBody}",
                apiUrl,
                businessJsonBody);

            // 发送请求 (使用 PostAsync 发送 JSON 请求体)
            var content = new StringContent(businessJsonBody, Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(apiUrl, content);

            // 确保请求成功
            response.EnsureSuccessStatusCode();

            // 解析响应
            var result = await response.Content.ReadFromJsonAsync<WeightPushResponseV2>();

            // 记录响应详情
            if (result == null)
            {
                Log.Error("旺店通重量回传响应解析失败，返回null");
                return new WeightPushResponseV2
                {
                    Code = "-998",
                    Message = "响应解析失败"
                };
            }

            if (result.IsSuccess)
                Log.Information(
                    "旺店通重量回传成功V2: 物流单号={LogisticsNo}, 仓储单号={SrcOrderNo}, 重量={Weight}kg, 通道号={ExportNum}, 响应数据={@Response}",
                    request.LogisticsNo, request.SrcOrderNo, request.Weight, result.ExportNum, result);
            else
                Log.Warning(
                    "旺店通重量回传失败V2: 物流单号={LogisticsNo}, 仓储单号={SrcOrderNo}, 重量={Weight}kg, 错误码={Code}, 错误信息={Message}, 响应数据={@Response}",
                    request.LogisticsNo, request.SrcOrderNo, request.Weight, result.Code, result.Message, result);

            return result;
        }
        catch (ArgumentException argEx)
        {
            Log.Error(argEx, "旺店通重量回传请求参数错误V2: 物流单号={LogisticsNo}, 仓储单号={SrcOrderNo}, 错误={Message}",
                request.LogisticsNo, request.SrcOrderNo, argEx.Message);
            return new WeightPushResponseV2
            {
                Code = "-997",
                Message = $"请求参数错误: {argEx.Message}"
            };
        }
        catch (HttpRequestException ex)
        {
            Log.Error(ex, "旺店通重量回传HTTP请求异常V2: URL={Url}, 业务请求体={BusinessBody}, 异常={ErrorMessage}",
                apiUrl ?? "N/A",
                businessJsonBody ?? "N/A",
                ex.Message);
            return new WeightPushResponseV2
            {
                Code = "-99",
                Message = $"HTTP请求异常: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "旺店通重量回传发生异常V2: URL={Url}, 业务请求体={BusinessBody}, 异常={ErrorMessage}",
                apiUrl ?? "N/A",
                businessJsonBody ?? "N/A",
                ex.Message);
            return new WeightPushResponseV2
            {
                Code = "-999",
                Message = $"系统异常: {ex.Message}"
            };
        }
    }

    /// <summary>
    ///     计算API签名 (适配V2接口参数和签名算法)
    /// </summary>
    /// <param name="publicParameters">公共参数字典 (已包含method, timestamp, format等，不含sign)</param>
    /// <param name="businessJsonBody">业务参数的JSON字符串</param>
    /// <param name="secret">接口私钥 appsecret</param>
    /// <returns>32位MD5大写签名</returns>
    private static string CalculateSign(Dictionary<string, string> publicParameters, string businessJsonBody,
        string secret)
    {
        // 第一步：将公共参数的字段名字按字典序从小到大排序
        var sortedPublicParams = publicParameters.OrderBy(p => p.Key);

        // 第二步：拼接排序后的公共参数的 key、value (无分隔符)
        var publicParamString = new StringBuilder();
        foreach (var param in sortedPublicParams)
        {
            publicParamString.Append(param.Key);
            publicParamString.Append(param.Value);
        }

        // 第三步：在第二步的结果后面拼接请求体 (JSON字符串)
        publicParamString.Append(businessJsonBody);

        // 第四步：在第三步的结果的【首尾】拼接 appsecret
        var signStringWithSecret = $"{secret}{publicParamString}{secret}";

        // 第五步：生成32位MD5大写签名值
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(signStringWithSecret));
        var md5String = BitConverter.ToString(hash).Replace("-", "").ToUpper(); // 转为大写

        return md5String;
    }
}