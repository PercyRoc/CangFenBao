using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Serilog;
using ShanghaiModuleBelt.Models.Sto;
using ShanghaiModuleBelt.Models.Sto.Settings;
using Common.Services.Settings;

namespace ShanghaiModuleBelt.Services.Sto;

/// <summary>
/// 申通仓客户出库自动揽收接口服务实现
/// </summary>
public class StoAutoReceiveService(
    HttpClient httpClient,
    ISettingsService settingsService)
    : IStoAutoReceiveService
{
    /// <summary>
    /// 发送自动揽收请求
    /// </summary>
    /// <param name="request">申通自动揽收请求</param>
    /// <returns>申通自动揽收响应</returns>
    public async Task<StoAutoReceiveResponse?> SendAutoReceiveRequestAsync(StoAutoReceiveRequest request)
    {
        var settings = settingsService.LoadSettings<StoApiSettings>();

        if (string.IsNullOrEmpty(settings.ApiUrl) || string.IsNullOrEmpty(settings.FromAppkey) ||
            string.IsNullOrEmpty(settings.FromCode) || string.IsNullOrEmpty(settings.AppSecret))
        {
            Log.Error("申通API配置不完整，请检查ApiUrl, FromAppkey, FromCode, AppSecret。");
            return new StoAutoReceiveResponse
            {
                Success = false,
                ErrorMsg = "申通API配置不完整"
            };
        }

        var contentJson = JsonConvert.SerializeObject(request);

        // TODO: 根据申通文档补充 data_digest 签名逻辑
        // 通常签名会涉及：公共参数 + 业务报文体 + app_secret 组成的字符串进行 MD5 或 SHA256 签名
        var dataDigest = CalculateDataDigest(contentJson, settings.AppSecret);

        var apiRequest = new
        {
            api_name = settings.ApiName,
            content = contentJson,
            from_appkey = settings.FromAppkey,
            from_code = settings.FromCode,
            to_appkey = settings.ToAppkey,
            to_code = settings.ToCode,
            data_digest = dataDigest
        };

        var requestJson = JsonConvert.SerializeObject(apiRequest);

        try
        {
            Log.Information("发送申通自动揽收请求到 {ApiUrl}，请求内容：{RequestJson}", settings.ApiUrl, requestJson);

            var httpContent = new StringContent(requestJson, Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(settings.ApiUrl, httpContent);
            response.EnsureSuccessStatusCode(); // 确保HTTP状态码是成功的

            var responseContent = await response.Content.ReadAsStringAsync();
            Log.Information("收到申通自动揽收响应：{ResponseContent}", responseContent);

            var stoResponse = JsonConvert.DeserializeObject<StoAutoReceiveResponse>(responseContent);
            return stoResponse;
        }
        catch (HttpRequestException ex)
        {
            Log.Error(ex, "申通自动揽收请求失败：{Message}", ex.Message);
            return new StoAutoReceiveResponse
            {
                Success = false,
                ErrorMsg = $"网络请求失败：{ex.Message}"
            };
        }
        catch (Newtonsoft.Json.JsonException ex)
        {
            Log.Error(ex, "解析申通自动揽收响应失败：{Message}", ex.Message);
            return new StoAutoReceiveResponse
            {
                Success = false,
                ErrorMsg = $"解析响应失败：{ex.Message}"
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "申通自动揽收发生未知错误：{Message}", ex.Message);
            return new StoAutoReceiveResponse
            {
                Success = false,
                ErrorMsg = $"未知错误：{ex.Message}"
            };
        }
    }

    /// <summary>
    /// 计算 data_digest 签名
    /// TODO: 详细签名规则待申通文档确认
    /// </summary>
    /// <param name="content">业务报文体JSON字符串</param>
    /// <param name="appSecret">AppSecret</param>
    /// <returns>签名字符串</returns>
    private static string CalculateDataDigest(string content, string appSecret)
    {
        // 示例：这里只是一个简单的占位符，实际签名可能更复杂
        // 假设签名为 content + appSecret 的 MD5 哈希
        var signSource = $"{content}{appSecret}";
        using var md5 = MD5.Create();
        var hashBytes = md5.ComputeHash(Encoding.UTF8.GetBytes(signSource));
        return Convert.ToBase64String(hashBytes);
    }
} 