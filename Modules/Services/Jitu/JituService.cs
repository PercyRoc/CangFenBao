using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Common.Services.Settings;
using Modules.Models.Jitu;
using Modules.Models.Jitu.Settings;
using Newtonsoft.Json;
using Serilog;

namespace Modules.Services.Jitu
{
    public class JituService : IJituService
    {
        private readonly HttpClient _httpClient;
        private readonly ISettingsService _settingsService;
        private JituApiSettings _jituApiSettings;

        public JituService(ISettingsService settingsService)
        {
            _settingsService = settingsService;
            _httpClient = new HttpClient();
            LoadSettings();
        }

        private void LoadSettings()
        {
            _jituApiSettings = _settingsService.LoadSettings<JituApiSettings>();
            Log.Information("JituApiSettings loaded. OpScanUrl: {OpScanUrl}", _jituApiSettings.OpScanUrl);
        }

        public async Task<JituOpScanResponse> SendOpScanRequestAsync(JituOpScanRequest request)
        {
            if (string.IsNullOrEmpty(_jituApiSettings.OpScanUrl))
            {
                Log.Warning("Jitu OpScan URL is not configured.");
                return new JituOpScanResponse { Success = false, Code = 811, Message = "服务关闭：极兔OpScan URL未配置" };
            }

            try
            {
                var jsonContent = JsonConvert.SerializeObject(request);
                var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                Log.Information("Sending Jitu OpScan request to {Url} with content: {Content}", _jituApiSettings.OpScanUrl, jsonContent);
                var response = await _httpClient.PostAsync(_jituApiSettings.OpScanUrl, httpContent);
                response.EnsureSuccessStatusCode();

                var responseBody = await response.Content.ReadAsStringAsync();
                Log.Information("Received Jitu OpScan response: {ResponseBody}", responseBody);

                var jituResponse = JsonConvert.DeserializeObject<JituOpScanResponse>(responseBody);
                return jituResponse;
            }
            catch (HttpRequestException httpEx)
            {
                Log.Error(httpEx, "Jitu OpScan HTTP request failed: {Message}", httpEx.Message);
                return new JituOpScanResponse { Success = false, Code = 812, Message = $"服务暂停：HTTP请求失败 ({httpEx.Message})" };
            }
            catch (JsonException jsonEx)
            {
                Log.Error(jsonEx, "Failed to deserialize Jitu OpScan response: {Message}", jsonEx.Message);
                return new JituOpScanResponse { Success = false, Code = 812, Message = $"服务暂停：响应解析失败 ({jsonEx.Message})" };
            }
            catch (System.Exception ex)
            {
                Log.Error(ex, "An unexpected error occurred during Jitu OpScan request: {Message}", ex.Message);
                return new JituOpScanResponse { Success = false, Code = 812, Message = $"服务暂停：未知错误 ({ex.Message})" };
            }
        }
    }
} 