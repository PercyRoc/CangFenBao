using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
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

            if (string.IsNullOrEmpty(settings.ApiUrl) || string.IsNullOrEmpty(settings.FromAppKey) ||
                string.IsNullOrEmpty(settings.FromCode) || string.IsNullOrEmpty(settings.AppSecret))
            {
                Log.Error("申通API配置不完整，请检查ApiUrl, FromAppKey, FromCode, AppSecret。");
                return new ShenTongLanShouResponse
                {
                    Success = false,
                    ErrorMsg = "申通API配置不完整"
                };
            }

            var contentJson = JsonSerializer.Serialize(request);

            // 计算 data_digest 签名，使用官方文档的算法
            var dataDigest = CalculateDataDigest(contentJson, settings.AppSecret);

            try
            {
                Log.Information("发送申通自动揽收请求到 {ApiUrl}，content内容：{Content}", settings.ApiUrl, contentJson);

                // 将所有公共参数和content一起作为表单数据发送
                var formContent = new FormUrlEncodedContent(
                [
                    new KeyValuePair<string, string>("api_name", settings.ApiName!),
                    new KeyValuePair<string, string>("content", contentJson),
                    new KeyValuePair<string, string>("from_appkey", settings.FromAppKey!),
                    new KeyValuePair<string, string>("from_code", settings.FromCode!),
                    new KeyValuePair<string, string>("to_appkey", settings.ToAppkey!),
                    new KeyValuePair<string, string>("to_code", settings.ToCode!),
                    new KeyValuePair<string, string>("data_digest", dataDigest)
                ]);

                var response = await httpClient.PostAsync(settings.ApiUrl, formContent);
                response.EnsureSuccessStatusCode(); // 确保HTTP状态码是成功的

                var responseContent = await response.Content.ReadAsStringAsync();
                Log.Information("收到申通自动揽收响应：{ResponseContent}", responseContent);

                // 使用 JsonSerializer 反序列化JSON响应
                var stoResponse = JsonSerializer.Deserialize<ShenTongLanShouResponse>(responseContent);

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
            catch (System.Text.Json.JsonException ex)
            {
                Log.Error(ex, "解析申通自动揽收响应失败：{Message}", ex.Message);
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
    }
} 