using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Common.Services.Settings;
using Newtonsoft.Json;
using Serilog;
using ShanghaiModuleBelt.Models.Yunda;
using ShanghaiModuleBelt.Models.Yunda.Settings;

namespace ShanghaiModuleBelt.Services.Yunda;

/// <summary>
///     韵达上传重量接口服务实现
/// </summary>
public class YundaUploadWeightService(
    HttpClient httpClient,
    ISettingsService settingsService)
    : IYundaUploadWeightService
{
    /// <summary>
    ///     发送上传重量请求
    /// </summary>
    /// <param name="request">韵达上传重量请求</param>
    /// <returns>韵达上传重量响应</returns>
    public async Task<YundaUploadWeightResponse?> SendUploadWeightRequestAsync(YundaUploadWeightRequest request)
    {
        var settings = settingsService.LoadSettings<YundaApiSettings>();

        if (string.IsNullOrEmpty(settings.ApiUrl) || string.IsNullOrEmpty(settings.AppKey) ||
            string.IsNullOrEmpty(settings.AppSecret) || string.IsNullOrEmpty(settings.PartnerId) ||
            string.IsNullOrEmpty(settings.Password) || string.IsNullOrEmpty(settings.Rc4Key))
        {
            Log.Error("韵达API配置不完整，请检查ApiUrl, AppKey, AppSecret, PartnerId, Password, Rc4Key。" +
                      "当前配置：ApiUrl={ApiUrl}, AppKey={AppKey}, AppSecret={AppSecret}, PartnerId={PartnerId}, Password={Password}, Rc4Key={Rc4Key}",
                settings.ApiUrl, settings.AppKey, settings.AppSecret, settings.PartnerId, settings.Password, settings.Rc4Key);
            return new YundaUploadWeightResponse
            {
                Result = false,
                Code = "9999",
                Message = "韵达API配置不完整"
            };
        }

        var contentJson = JsonConvert.SerializeObject(request.Orders);

        // TODO: 韵达的签名说明参看附录《鉴权说明》，此处为MD5 + Base64 占位，实际需要根据RC4加密规则进行调整。
        // 韵达鉴权说明：所有报文体（包括公共参数、业务参数、sign）都拼接成一个字符串（JSON），
        // 然后使用 partnerid+password+rc4Key 对该字符串进行RC4加密。
        // 鉴于RC4在.NET Core中需要额外实现或库，此处暂用MD5+Base64占位
        var requestBody = new
        {
            partnerid = request.PartnerId,
            password = request.Password,
            rc4Key = request.Rc4Key,
            orders = request.Orders
        };

        var requestJson = JsonConvert.SerializeObject(requestBody);

        var sign = CalculateSign(requestJson, settings.AppSecret); // 使用完整的请求体JSON进行签名
        var reqTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();

        try
        {
            Log.Information("发送韵达上传重量请求到 {ApiUrl}，请求内容：{RequestJson}", settings.ApiUrl, requestJson);

            var httpContent = new StringContent(requestJson, Encoding.UTF8, "application/json");
            httpClient.DefaultRequestHeaders.Clear();
            httpClient.DefaultRequestHeaders.Add("app-key", settings.AppKey);
            httpClient.DefaultRequestHeaders.Add("sign", sign);
            httpClient.DefaultRequestHeaders.Add("req-time", reqTime.ToString());

            var response = await httpClient.PostAsync(settings.ApiUrl, httpContent);
            response.EnsureSuccessStatusCode(); // 确保HTTP状态码是成功的

            var responseContent = await response.Content.ReadAsStringAsync();
            Log.Information("收到韵达上传重量响应：{ResponseContent}", responseContent);

            var yundaResponse = JsonConvert.DeserializeObject<YundaUploadWeightResponse>(responseContent);
            return yundaResponse;
        }
        catch (HttpRequestException ex)
        {
            Log.Error(ex, "韵达上传重量请求失败：{Message}", ex.Message);
            return new YundaUploadWeightResponse
            {
                Result = false,
                Code = "9999",
                Message = $"网络请求失败：{ex.Message}"
            };
        }
        catch (JsonException ex)
        {
            Log.Error(ex, "解析韵达上传重量响应失败：{Message}", ex.Message);
            return new YundaUploadWeightResponse
            {
                Result = false,
                Code = "9999",
                Message = $"解析响应失败：{ex.Message}"
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "韵达上传重量发生未知错误：{Message}", ex.Message);
            return new YundaUploadWeightResponse
            {
                Result = false,
                Code = "9999",
                Message = $"未知错误：{ex.Message}"
            };
        }
    }

    /// <summary>
    ///     计算 sign 签名
    ///     TODO: 详细签名规则待韵达文档确认，目前使用 MD5 + Base64 占位
    ///     韵达鉴权说明：所有报文体（包括公共参数、业务参数、sign）都拼接成一个字符串（JSON），
    ///     然后使用 partnerid+password+rc4Key 对该字符串进行RC4加密。
    ///     鉴于RC4在.NET Core中需要额外实现或库，此处暂用MD5+Base64占位
    /// </summary>
    /// <param name="content">业务报文体JSON字符串</param>
    /// <param name="appSecret">AppSecret</param>
    /// <returns>签名字符串</returns>
    private static string CalculateSign(string content, string appSecret)
    {
        // 韵达签名规则：MD5( RequstBody(请求参数对象).toJSONString() + "_" + app-secret);
        var signSource = $"{content}_{appSecret}";
        var hashBytes = MD5.HashData(Encoding.UTF8.GetBytes(signSource));

        // 将字节数组转换为十六进制字符串
        var sb = new StringBuilder();
        foreach (var t in hashBytes)
        {
            sb.Append(t.ToString("x2")); // "x2" 表示两位小写十六进制
        }
        return sb.ToString();
    }
}