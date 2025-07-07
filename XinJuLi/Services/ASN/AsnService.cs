using System.Windows;
using Serilog;
using XinJuLi.Models.ASN;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Common.Services.Notifications;
using Common.Services.Settings;
using XinJuLi.Events;

namespace XinJuLi.Services.ASN
{
    /// <summary>
    /// ASN服务实现
    /// </summary>
    public class AsnService(
        IDialogService dialogService,
        INotificationService notificationService,
        ISettingsService settingsService,
        IEventAggregator eventAggregator,
        IAsnCacheService asnCacheService,
        IAsnStorageService asnStorageService)
        : IAsnService
    {
        private static readonly JsonSerializerOptions CaseInsensitiveOptions =
            new() { PropertyNameCaseInsensitive = true };

        private static readonly JsonSerializerOptions CamelCaseOptions = new()
            { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        /// <summary>
        /// 处理ASN单数据
        /// </summary>
        public Response ProcessAsnOrderInfo(AsnOrderInfo asnInfo)
        {
            try
            {
                Log.Information("收到ASN单数据: {@AsnInfo}", new
                {
                    asnInfo.OrderCode,
                    asnInfo.CarCode,
                    ItemsCount = asnInfo.Items.Count
                });

                // 将ASN单添加到缓存
                asnCacheService.AddAsnOrder(asnInfo);

                // 保存ASN单到存储
                asnStorageService.SaveAsnOrder(asnInfo);

                // 发布ASN单已添加到缓存事件
                eventAggregator.GetEvent<AsnOrderAddedToCacheEvent>().Publish(asnInfo);

                return Response.CreateSuccess();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理ASN单数据时发生异常: {OrderCode}", asnInfo.OrderCode);
                return Response.CreateFailed($"处理失败: {ex.Message}", "INTERNAL_ERROR");
            }
        }

        /// <summary>
        /// 处理扫码复核请求
        /// </summary>
        public async Task<Response> ProcessMaterialReview(MaterialReviewRequest request)
        {
            try
            {
                // 获取服务器地址
                var settings = settingsService.LoadSettings<AsnSettings>();
                var reviewUrl = settings.ReviewServerUrl.Trim();

                if (string.IsNullOrWhiteSpace(reviewUrl))
                {
                    return Response.CreateFailed("未配置复核服务器地址", "NO_SERVER_URL");
                }

                // 从设置中获取月台值，并覆盖请求中的值
                request.ExitArea = settings.ReviewExitArea;

                using var httpClient = new HttpClient();
                var json = JsonSerializer.Serialize(request, CamelCaseOptions);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var httpResponse = await httpClient.PostAsync(reviewUrl, content);

                if (!httpResponse.IsSuccessStatusCode)
                {
                    return Response.CreateFailed($"服务器返回错误: {httpResponse.StatusCode}", "HTTP_ERROR");
                }

                var responseString = await httpResponse.Content.ReadAsStringAsync();

                var serverResponse = JsonSerializer.Deserialize<Response>(responseString, CaseInsensitiveOptions);

                return serverResponse ?? Response.CreateFailed("服务器响应解析失败", "PARSE_ERROR");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理扫码复核请求失败: {BoxCode}", request.BoxCode);
                return Response.CreateFailed($"处理扫码复核请求失败: {ex.Message}");
            }
        }
    }
}