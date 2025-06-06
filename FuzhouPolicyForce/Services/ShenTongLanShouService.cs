using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Serilog;
using Common.Services.Settings;
using FuzhouPolicyForce.Models;
using System.Text.Json;

namespace FuzhouPolicyForce.Services
{
    /// <summary>
    /// 申通仓客户出库自动揽收接口服务实现
    /// </summary>
    public class ShenTongLanShouService(
        HttpClient httpClient,
        ISettingsService settingsService)
    {
        /// <summary>
        /// 发送自动揽收请求
        /// </summary>
        /// <param name="request">申通自动揽收请求</param>
        /// <returns>申通自动揽收响应</returns>
        public async Task<ShenTongLanShouResponse?> UploadCangKuAutoAsync(ShenTongLanShouRequest request)
        {
            var settings = settingsService.LoadSettings<ShenTongLanShouConfig>();

            // 验证必填配置
            if (string.IsNullOrEmpty(settings.ApiUrl) || string.IsNullOrEmpty(settings.FromAppKey) ||
                string.IsNullOrEmpty(settings.FromCode) || string.IsNullOrEmpty(settings.AppSecret) ||
                string.IsNullOrEmpty(settings.WhCode) || string.IsNullOrEmpty(settings.OrgCode) ||
                string.IsNullOrEmpty(settings.UserCode))
            {
                Log.Error("申通API配置不完整，请检查ApiUrl, FromAppKey, FromCode, AppSecret, WhCode, OrgCode, UserCode。");
                return new ShenTongLanShouResponse
                {
                    Success = false,
                    ErrorMsg = "申通API配置不完整"
                };
            }

            Log.Debug("申通配置参数：FromAppKey={FromAppKey}, FromCode={FromCode}, ToAppkey={ToAppkey}, ToCode={ToCode}", 
                settings.FromAppKey, settings.FromCode, settings.ToAppkey, settings.ToCode);

            // 设置配置中的必填字段到请求对象
            request.WhCode = settings.WhCode;
            request.OrgCode = settings.OrgCode;
            request.UserCode = settings.UserCode;

            var contentJson = JsonSerializer.Serialize(request);
            Log.Debug("申通请求Content内容：{Content}", contentJson);

            // 计算 data_digest 签名，使用官方文档的算法
            var dataDigest = CalculateDataDigest(contentJson, settings.AppSecret);

            string? responseContent = null;
            try
            {
                Log.Information("发送申通自动揽收请求到 {ApiUrl}，content内容：{Content}", settings.ApiUrl, contentJson);

                // 将所有公共参数和content一起作为表单数据发送（不是JSON）
                var formContent = new FormUrlEncodedContent(
                [
                    new KeyValuePair<string, string>("api_name", "GALAXY_CANGKU_AUTO_NEW"),
                    new KeyValuePair<string, string>("content", contentJson),
                    new KeyValuePair<string, string>("from_appkey", settings.FromAppKey),
                    new KeyValuePair<string, string>("from_code", settings.FromCode),
                    new KeyValuePair<string, string>("to_appkey", settings.ToAppkey ?? "galaxy_receive"),
                    new KeyValuePair<string, string>("to_code", settings.ToCode ?? "galaxy_receive"),
                    new KeyValuePair<string, string>("data_digest", dataDigest)
                ]);

                Log.Debug("申通请求参数：api_name=GALAXY_CANGKU_AUTO_NEW, from_appkey={FromAppKey}, to_appkey={ToAppkey}", 
                    settings.FromAppKey, settings.ToAppkey ?? "galaxy_receive");

                var response = await httpClient.PostAsync(settings.ApiUrl, formContent);
                response.EnsureSuccessStatusCode(); // 确保HTTP状态码是成功的

                responseContent = await response.Content.ReadAsStringAsync();
                Log.Information("收到申通自动揽收响应：{ResponseContent}", responseContent);

                // 尝试解析响应（可能是JSON或XML格式）
                var stoResponse = ParseResponse(responseContent);
                
                Log.Debug("申通响应解析结果：Success={Success}, ErrorMsg={ErrorMsg}, ErrorCode={ErrorCode}, RespCode={RespCode}, ResMessage={ResMessage}", 
                    stoResponse?.Success, stoResponse?.ErrorMsg, stoResponse?.ErrorCode, stoResponse?.Data?.RespCode, stoResponse?.Data?.ResMessage);

                return stoResponse;
            }
            catch (HttpRequestException ex)
            {
                Log.Error(ex, "申通自动揽收请求失败：{Message}", ex.Message);
                return new ShenTongLanShouResponse
                {
                    Success = false,
                    ErrorMsg = $"网络请求失败：{ex.Message}"
                };
            }
            catch (JsonException ex)
            {
                Log.Error(ex, "解析申通自动揽收响应失败：{Message}, 原始响应内容：{ResponseContent}", ex.Message, responseContent ?? "未获取到响应内容");
                return new ShenTongLanShouResponse
                {
                    Success = false,
                    ErrorMsg = $"解析响应失败：{ex.Message}"
                };
            }
            catch (Exception ex)
            {
                Log.Error(ex, "申通自动揽收发生未知错误：{Message}", ex.Message);
                return new ShenTongLanShouResponse
                {
                    Success = false,
                    ErrorMsg = $"未知错误：{ex.Message}"
                };
            }
        }

        /// <summary>
        /// 计算 data_digest 签名
        /// 根据申通官方文档：content + secretKey 的 MD5 哈希后进行 Base64 编码
        /// </summary>
        /// <param name="content">业务报文体JSON字符串</param>
        /// <param name="appSecret">AppSecret</param>
        /// <returns>签名字符串</returns>
        private static string CalculateDataDigest(string content, string appSecret)
        {
            // 按照申通官方文档的签名算法：content + secretKey
            var toSignContent = content + appSecret;
            
            Log.Information("申通签名原始字符串：{SignSource}", toSignContent);
            
            var hashBytes = MD5.HashData(Encoding.UTF8.GetBytes(toSignContent));
            var base64String = Convert.ToBase64String(hashBytes);
            
            Log.Information("申通签名结果：Base64={Base64}", base64String);
            
            return base64String; // 不进行 URL 编码，让 HttpClient 自动处理
        }

        /// <summary>
        /// 解析响应（可能是JSON或XML格式）
        /// </summary>
        /// <param name="responseContent">响应内容</param>
        /// <returns>解析后的响应对象</returns>
        private ShenTongLanShouResponse? ParseResponse(string responseContent)
        {
            if (string.IsNullOrWhiteSpace(responseContent))
            {
                Log.Warning("申通响应内容为空");
                return new ShenTongLanShouResponse
                {
                    Success = false,
                    ErrorMsg = "响应内容为空"
                };
            }

            try
            {
                // 首先尝试作为JSON解析
                if (responseContent.TrimStart().StartsWith("{"))
                {
                    Log.Debug("尝试解析JSON格式响应");
                    return JsonSerializer.Deserialize<ShenTongLanShouResponse>(responseContent);
                }
                // 尝试作为XML解析
                else if (responseContent.TrimStart().StartsWith("<"))
                {
                    Log.Debug("尝试解析XML格式响应");
                    return ParseXmlResponse(responseContent);
                }
                else
                {
                    Log.Warning("无法识别的响应格式：{ResponseContent}", responseContent);
                    return new ShenTongLanShouResponse
                    {
                        Success = false,
                        ErrorMsg = "无法识别的响应格式"
                    };
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "解析申通响应失败：{Message}", ex.Message);
                return new ShenTongLanShouResponse
                {
                    Success = false,
                    ErrorMsg = $"解析响应失败：{ex.Message}"
                };
            }
        }

        /// <summary>
        /// 解析XML格式的响应
        /// </summary>
        /// <param name="xmlContent">XML响应内容</param>
        /// <returns>解析后的响应对象</returns>  
        private ShenTongLanShouResponse ParseXmlResponse(string xmlContent)
        {
            var xDoc = XDocument.Parse(xmlContent);
            var responseElement = xDoc.Root;

            if (responseElement == null)
            {
                return new ShenTongLanShouResponse
                {
                    Success = false,
                    ErrorMsg = "XML响应根元素为空"
                };
            }

            var successElement = responseElement.Element("success");
            var errorCodeElement = responseElement.Element("errorCode");
            var errorMsgElement = responseElement.Element("errorMsg");

            var success = successElement?.Value?.ToLower() == "true";
            var errorCode = errorCodeElement?.Value ?? "";
            var errorMsg = errorMsgElement?.Value ?? "";

            return new ShenTongLanShouResponse
            {
                Success = success,
                ErrorCode = errorCode,
                ErrorMsg = errorMsg
            };
        }
    }
}