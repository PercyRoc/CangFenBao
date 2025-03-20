using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Common.Services.Settings;
using FuzhouPolicyForce.Models;
using Serilog;

namespace FuzhouPolicyForce.WangDianTong;

/// <summary>
/// 旺店通API服务实现
/// </summary>
public class WangDianTongApiService(HttpClient httpClient, ISettingsService settingsService) : IWangDianTongApiService
{
    /// <summary>
    /// 重量回传
    /// </summary>
    public async Task<WeightPushResponse> PushWeightAsync(
        string logisticsNo, 
        decimal weight, 
        bool isCheckWeight = false, 
        bool isCheckTradeStatus = false, 
        string packagerNo = "")
    {
        Dictionary<string, string> requestParams = [];
        try
        {
            // 获取旺店通配置
            var settings = settingsService.LoadSettings<WangDianTongSettings>();
            
            // 构建请求参数
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            requestParams = new Dictionary<string, string>
            {
                { "sid", settings.SellerAccount },
                { "appkey", settings.ApiAccount },
                { "timestamp", timestamp.ToString() },
                { "logistics_no", logisticsNo },
                { "weight", weight.ToString(CultureInfo.CurrentCulture) },
                { "is_setting", isCheckWeight ? "1" : "0" },
                { "is_check_trade_status", isCheckTradeStatus ? "1" : "0" }
            };

            // 如果有打包员编号，则添加
            if (!string.IsNullOrEmpty(packagerNo))
            {
                requestParams.Add("packager_no", packagerNo);
            }

            // 计算签名
            var sign = CalculateSign(requestParams, settings.ApiSecret);
            requestParams.Add("sign", sign);

            // 构建请求URL
            var apiUrl = $"{settings.GetApiBaseUrl()}vip_stockout_sales_weight_push.php";
            
            // 记录请求详情
            Log.Information("旺店通重量回传请求: URL={Url}, 参数={@Params}", 
                apiUrl, 
                requestParams.Where(p => p.Key != "sign").ToDictionary(p => p.Key, p => p.Value));

            // 构建表单内容
            var formContent = new FormUrlEncodedContent(requestParams);
            
            // 发送请求
            var response = await httpClient.PostAsync(apiUrl, formContent);
            
            // 确保请求成功
            response.EnsureSuccessStatusCode();
            
            // 解析响应
            var result = await response.Content.ReadFromJsonAsync<WeightPushResponse>();
            
            // 记录响应详情
            if (result!.IsSuccess)
            {
                Log.Information("旺店通重量回传成功: 物流单号={LogisticsNo}, 重量={Weight}g, 物流名称={LogisticsName}, 响应数据={@Response}",
                    logisticsNo, weight, result.LogisticsName, result);
            }
            else
            {
                Log.Warning("旺店通重量回传失败: 物流单号={LogisticsNo}, 重量={Weight}kg, 错误码={Code}, 错误信息={Message}, 响应数据={@Response}",
                    logisticsNo, weight, result.Code, result.Message, result);
            }
            
            return result;
        }
        catch (HttpRequestException ex)
        {
            Log.Error(ex, "旺店通重量回传HTTP请求异常: 物流单号={LogisticsNo}, 请求URL={Url}, 请求参数={@Params}", 
                logisticsNo, 
                $"{settingsService.LoadSettings<WangDianTongSettings>().GetApiBaseUrl()}vip_stockout_sales_weight_push.php",
                requestParams.Where(p => p.Key != "sign").ToDictionary(p => p.Key, p => p.Value));
            return new WeightPushResponse
            {
                Code = -99,
                Message = $"HTTP请求异常: {ex.Message}"
            };
        }
        catch (Exception ex) 
        {
            Log.Error(ex, "旺店通重量回传发生异常: 物流单号={LogisticsNo}, 请求URL={Url}, 请求参数={@Params}", 
                logisticsNo, 
                $"{settingsService.LoadSettings<WangDianTongSettings>().GetApiBaseUrl()}vip_stockout_sales_weight_push.php",
                requestParams.Where(p => p.Key != "sign").ToDictionary(p => p.Key, p => p.Value));
            return new WeightPushResponse
            {
                Code = -999,
                Message = $"系统异常: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// 计算API签名
    /// </summary>
    private static string CalculateSign(Dictionary<string, string> parameters, string secret)
    {
        // 按照键名排序参数
        var sortedParams = parameters.OrderBy(p => p.Key);

        // 处理每个参数
        var signParts = sortedParams.Select(param =>
        {
            // 1. 计算键名长度（保留2位）
            var keyLength = param.Key.Length.ToString("D2");

            // 2. 计算值长度（保留4位）
            var valueLength = param.Value.Length.ToString("D4");

            // 3. 拼接格式：keyLength-key:valueLength-value
            return $"{keyLength}-{param.Key}:{valueLength}-{param.Value}";
        });

        // 连接所有参数，用分号分隔
        var signString = string.Join(";", signParts);

        // 添加密钥
        var signStringWithSecret = $"{signString}{secret}";

        // 计算MD5（32位小写）
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(signStringWithSecret));
        var md5String = BitConverter.ToString(hash).Replace("-", "").ToLower();

        return md5String;
    }
} 