using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommonLibrary.Models;
using CommonLibrary.Services;
using Presentation_CommonLibrary.Services;
using Presentation_KuaiLv.Models.DWS;
using Presentation_KuaiLv.Models.Settings.Upload;
using Presentation_KuaiLv.Services.Warning;
using Serilog;

namespace Presentation_KuaiLv.Services.DWS;

/// <summary>
///     DWS服务实现
/// </summary>
public class DwsService(
    HttpClient httpClient,
    ISettingsService settingsService,
    INotificationService notificationService,
    IWarningLightService warningLightService)
    : IDwsService
{
    private const int MaxFailureCount = 5;
    private int _failureCount;

    /// <inheritdoc />
    public async Task<DwsResponse> ReportPackageAsync(PackageInfo package)
    {
        try
        {
            // 加载配置
            var config = settingsService.LoadConfiguration<UploadConfiguration>();

            // 构建请求数据
            var request = new DwsRequest
            {
                BarCode = string.IsNullOrEmpty(package.Barcode) ? "noread" : package.Barcode,
                Weight = package.Weight,
                Length = package.Length ?? 0,
                Width = package.Width ?? 0,
                Height = package.Height ?? 0,
                Volume = package.Volume ?? 0,
                Timestamp = package.CreateTime.ToString("yyyy-MM-dd HH:mm:ss")
            };

            // 序列化请求数据
            var jsonContent = JsonSerializer.Serialize(request);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            // 计算Content-MD5
            var contentMd5 = Convert.ToBase64String(MD5.HashData(Encoding.UTF8.GetBytes(jsonContent)));

            // 构建签名字符串
            var timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString();
            var path = "/haina/parcel/dws/weight/w/bind";
            var stringToSign = $"POST\n{contentMd5}\n{path}";

            // 计算签名
            var secret = config.Secret;
            var signature = Convert.ToBase64String(new HMACSHA256(Encoding.UTF8.GetBytes(secret))
                .ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

            // 设置请求头
            httpClient.DefaultRequestHeaders.Clear();
            httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
            httpClient.DefaultRequestHeaders.Add("S-Ca-App", "kl_dws_weighing");
            httpClient.DefaultRequestHeaders.Add("S-Ca-Timestamp", timestamp);
            httpClient.DefaultRequestHeaders.Add("S-Ca-Signature", signature);
            httpClient.DefaultRequestHeaders.Add("Content-MD5", contentMd5);

            // 设置基地址
            httpClient.BaseAddress = new Uri(config.Environment == UploadEnvironment.Production
                ? "http://klwms.bb.sankuai.com"
                : "http://klwms.bb.test.sankuai.com");

            // 发送请求
            var response = await httpClient.PostAsync(path, content);
            var responseContent = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<DwsResponse>(responseContent);

            if (result == null)
            {
                Log.Error("DWS服务响应解析失败");
                notificationService.ShowError("DWS服务异常", "服务器响应解析失败");
                return new DwsResponse { Code = 500, Message = "服务器响应解析失败" };
            }

            // 处理不同的响应状态
            switch (result.Code)
            {
                case 200 when string.IsNullOrEmpty(result.Message):
                    // 包裹为SKU多退少补品
                    _failureCount = 0;
                    Log.Information("包裹上报成功：{Barcode}", package.Barcode);
                    notificationService.ShowSuccess("包裹上报成功", "绿灯");
                    await warningLightService.ShowGreenLightAsync();
                    return result;

                case 200:
                    // 包裹为SKU非多退少补品
                    _failureCount = 0;
                    Log.Information("包裹上报成功：{Barcode}", package.Barcode);
                    notificationService.ShowSuccess("包裹上报成功", "绿灯");
                    await warningLightService.ShowGreenLightAsync();
                    return result;

                case 1003:
                    // 包裹号无效
                    Log.Warning("包裹号无效：{Barcode}", package.Barcode);
                    notificationService.ShowWarning("包裹号无效", "红灯，包裹号无效");
                    await warningLightService.ShowRedLightAsync();
                    package.SetError($"包裹号无效：{result.Message}");
                    break;

                case 1005:
                    // 非履约日包裹
                    Log.Warning("非履约日包裹：{Barcode}", package.Barcode);
                    notificationService.ShowWarning("非履约日包裹", "红灯，非履约日包裹");
                    await warningLightService.ShowRedLightAsync();
                    package.SetError($"非履约日包裹：{result.Message}");
                    break;

                case 1004:
                    // 重量异常
                    Log.Warning("重量异常：{Barcode}, {Message}", package.Barcode, result.Message);
                    notificationService.ShowWarning("重量异常", "红灯，重量异常");
                    await warningLightService.ShowRedLightAsync();
                    package.SetError($"重量异常：{result.Message}");
                    break;

                case 400:
                    // 客户端请求错误
                    Log.Warning("客户端请求错误：{Message}", result.Message);
                    notificationService.ShowError("请求错误", "红灯，客户端请求错误");
                    await warningLightService.ShowRedLightAsync();
                    package.SetError($"请求错误：{result.Message}");
                    break;

                case 500:
                    // 服务端异常
                    Log.Error("服务端异常：{Message}", result.Message);
                    notificationService.ShowError("服务异常", "红灯，系统错误");
                    await warningLightService.ShowRedLightAsync();
                    package.SetError($"服务异常：{result.Message}");
                    break;

                default:
                    // 未知错误
                    Log.Error("未知错误：Code={Code}, Message={Message}", result.Code, result.Message);
                    notificationService.ShowError("未知错误", $"红灯，错误代码：{result.Code}");
                    await warningLightService.ShowRedLightAsync();
                    package.SetError($"未知错误：{result.Message}");
                    break;
            }

            // 处理失败计数
            _failureCount++;
            if (_failureCount < MaxFailureCount) return result;
            Log.Error("DWS服务连续失败次数达到{Count}次，需要停机", MaxFailureCount);
            notificationService.ShowError("DWS服务异常", "连续失败次数过多，设备将停机");
            await warningLightService.ShowRedLightAsync();
            // TODO: 发送停机指令

            return result;
        }
        catch (Exception ex)
        {
            _failureCount++;
            if (_failureCount >= MaxFailureCount)
            {
                Log.Error(ex, "DWS服务连续失败次数达到{Count}次，需要停机", MaxFailureCount);
                notificationService.ShowError("DWS服务异常", "连续失败次数过多，设备将停机");
                await warningLightService.ShowRedLightAsync();
                // TODO: 发送停机指令
            }

            Log.Error(ex, "DWS服务请求异常");
            return new DwsResponse { Code = 500, Message = ex.Message };
        }
    }
}