using System.Net.Http;
using System.Text;
using FuzhouPolicyForce.Models;
using Serilog;
using Common.Services.Settings;
using System.Text.Json;

namespace FuzhouPolicyForce.Services
{
    public class ShenTongLanShouService(ISettingsService settingsService)
    {
        private readonly HttpClient _httpClient = new();

        public async Task<string> UploadCangKuAutoAsync(CangKuAutoRequest request)
        {
            // 1. 获取配置
            var config = settingsService.LoadSettings<ShenTongLanShouConfig>();
            var contentJson = JsonSerializer.Serialize(request);
            var dataDigest = CalculateDigest(contentJson, config.SecretKey);

            // 2. 组装参数
            var dict = new Dictionary<string, string>
            {
                { "api_name", "GALAXY_CANGKU_AUTO_NEW" },
                { "content", contentJson },
                { "from_appkey", config.FromAppKey },
                { "from_code", config.FromCode },
                { "to_appkey", "galaxy_receive" },
                { "to_code", "galaxy_receive" },
                { "data_digest", dataDigest }
            };

            var content = new FormUrlEncodedContent(dict);

            // 3. 发送请求
            try
            {
                var response = await _httpClient.PostAsync(config.ApiUrl, content);
                var respStr = await response.Content.ReadAsStringAsync();
                Log.Information("申通仓库自动揽收接口响应: {Response}", respStr);
                return respStr;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "申通仓库自动揽收接口上传失败");
                throw;
            }
        }

        private static string CalculateDigest(string content, string? secretKey)
        {
            var toSignContent = content + secretKey;
            var inputBytes = Encoding.UTF8.GetBytes(toSignContent);
            var hash = System.Security.Cryptography.MD5.HashData(inputBytes);
            return Convert.ToBase64String(hash);
        }
    }
} 