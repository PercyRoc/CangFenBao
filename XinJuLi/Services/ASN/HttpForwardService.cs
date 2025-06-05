using System.Net.Http;
using System.Text;
using System.Text.Json;
using Common.Services.Settings;
using Serilog;
using XinJuLi.Models.ASN;

namespace XinJuLi.Services.ASN
{
    /// <summary>
    /// HTTP请求转发服务接口
    /// </summary>
    public interface IHttpForwardService
    {
        /// <summary>
        /// 转发请求到目标服务器
        /// </summary>
        /// <param name="endpoint">API端点</param>
        /// <param name="requestBody">请求体</param>
        /// <returns>转发是否成功</returns>
        Task<bool> ForwardRequestAsync(string endpoint, string requestBody);
    }

    /// <summary>
    /// HTTP请求转发服务实现
    /// </summary>
    public class HttpForwardService : IHttpForwardService, IDisposable
    {
        private readonly ISettingsService _settingsService;
        private readonly HttpClient _httpClient;

        public HttpForwardService(ISettingsService settingsService)
        {
            _settingsService = settingsService;
            _httpClient = new HttpClient();
        }

        /// <summary>
        /// 转发请求到目标服务器
        /// </summary>
        /// <param name="endpoint">API端点（如：send_asn_order_info）</param>
        /// <param name="requestBody">请求体</param>
        /// <returns>转发是否成功</returns>
        public async Task<bool> ForwardRequestAsync(string endpoint, string requestBody)
        {
            try
            {
                var settings = _settingsService.LoadSettings<AsnSettings>();

                // 检查是否启用转发
                if (!settings.EnableForwarding)
                {
                    return true; // 不需要转发，视为成功
                }

                // 检查转发配置
                if (string.IsNullOrWhiteSpace(settings.ForwardServerUrl))
                {
                    Log.Warning("转发已启用但未配置目标服务器地址");
                    return false;
                }

                // 构建转发URL
                var baseUrl = settings.ForwardServerUrl.TrimEnd('/');
                var appName = settings.ForwardApplicationName.Trim('/');
                var targetUrl = $"{baseUrl}/{appName}/{endpoint.TrimStart('/')}";

                Log.Information("转发请求到目标服务器: {TargetUrl}", targetUrl);

                // 设置超时
                _httpClient.Timeout = TimeSpan.FromSeconds(settings.ForwardTimeoutSeconds);

                // 创建请求内容
                using var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

                // 发送POST请求
                using var response = await _httpClient.PostAsync(targetUrl, content);

                if (response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    Log.Information("请求转发成功，状态码: {StatusCode}", response.StatusCode);
                    Log.Debug("转发响应内容: {ResponseBody}", responseBody);
                    return true;
                }
                else
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    Log.Warning("请求转发失败，状态码: {StatusCode}, 响应: {ErrorBody}", 
                        response.StatusCode, errorBody);
                    return false;
                }
            }
            catch (HttpRequestException ex)
            {
                Log.Error(ex, "转发请求时发生网络异常");
                return false;
            }
            catch (TaskCanceledException ex)
            {
                Log.Error(ex, "转发请求超时");
                return false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "转发请求时发生未预期异常");
                return false;
            }
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            _httpClient?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
} 