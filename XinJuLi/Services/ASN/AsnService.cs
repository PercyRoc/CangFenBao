using System.Windows;
using Common.Services.Ui;
using Serilog;
using XinJuLi.Models.ASN;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
        IEventAggregator eventAggregator)
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
                    ItemsCount = asnInfo.Items
                });

                // 必须在UI线程上弹出对话框
                Application.Current.Dispatcher.Invoke(() =>
                {
                    // 显示确认对话框
                    var parameters = new DialogParameters
                    {
                        { "AsnOrderInfo", asnInfo }
                    };

                    dialogService.ShowDialog("AsnOrderConfirmDialog", parameters, result =>
                    {
                        if (result.Result == ButtonResult.OK)
                        {
                            // 确认后发布事件通知ViewModel缓存数据
                            eventAggregator.GetEvent<AsnOrderReceivedEvent>().Publish(asnInfo);
                            notificationService.ShowSuccess($"已确认ASN单：{asnInfo.OrderCode}");
                        }
                        else
                        {
                            // 取消
                            notificationService.ShowWarning($"已取消ASN单：{asnInfo.OrderCode}");
                        }
                    });
                });

                return Response.CreateSuccess();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理ASN单数据失败: {OrderCode}", asnInfo.OrderCode);
                return Response.CreateFailed($"处理ASN单数据失败: {ex.Message}");
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