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
                barCode = string.IsNullOrEmpty(package.Barcode) ? "noread" : package.Barcode,
                weight = Math.Round(package.Weight, 2),
                length = package.Length ?? 0,
                width = package.Width ?? 0,
                height = package.Height ?? 0,
                volume = package.Volume ?? 0,
                timestamp = package.CreateTime.ToString("yyyy-MM-dd HH:mm:ss")
            };

            // 序列化请求数据
            var jsonContent = JsonSerializer.Serialize(request);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            // 计算Content-MD5 - 正确计算一次并使用Base64格式
            var contentMd5 = Convert.ToBase64String(MD5.HashData(Encoding.UTF8.GetBytes(jsonContent)));
            content.Headers.ContentMD5 = Convert.FromBase64String(contentMd5);

            // 构建签名字符串 - 确保路径一致
            var timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString();
            // 统一使用相同的API路径
            var path = "/haina/parcel/dws/w/bindWeight";
            var stringToSign = $"POST\n{contentMd5}\n{path}";

            // 计算签名
            var secret = config.Secret;
            var signature = Convert.ToBase64String(new HMACSHA256(Encoding.UTF8.GetBytes(secret))
                .ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

            // 设置基地址 - 保持不变
            var baseAddress = config.Environment == UploadEnvironment.Production
                ? "https://klwms.meituan.com"
                : "https://klvwms.meituan.com";
            // 不要修改httpClient的BaseAddress属性
            // httpClient.BaseAddress = new Uri(baseAddress);

            // 记录请求前的信息
            Log.Information("DWS请求参数: BaseUrl={BaseUrl}, Path={Path}, Method={Method}", 
                baseAddress, path, "POST");
            Log.Information("DWS请求头: Accept={Accept}, App={App}, Timestamp={Timestamp}, MD5={MD5}", 
                "application/json", "kl_dws_weighing", timestamp, contentMd5);
            Log.Information("DWS请求体: {RequestBody}", jsonContent);

            // 显式创建HttpRequestMessage确保使用POST方法
            // 使用完整URL
            var fullUrl = new Uri($"{baseAddress}{path}");
            Log.Information("DWS完整请求URL: {FullUrl}", fullUrl);
            
            var requestMessage = new HttpRequestMessage(HttpMethod.Post, fullUrl)
            {
                Content = content
            };
            
            // 设置请求头部
            requestMessage.Headers.Add("Accept", "application/json");
            requestMessage.Headers.Add("S-Ca-App", "kl_dws_weighing");
            requestMessage.Headers.Add("S-Ca-Timestamp", timestamp);
            requestMessage.Headers.Add("S-Ca-Signature", signature);
            
            // 发送请求，不使用默认请求头
            Log.Information("开始发送DWS请求...");
            var response = await httpClient.SendAsync(requestMessage);
            Log.Information("DWS响应状态码: {StatusCode}", response.StatusCode);
            
            var responseContent = await response.Content.ReadAsStringAsync();
            Log.Information("DWS响应内容: {ResponseContent}", responseContent);

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
                    await warningLightService.TurnOffGreenLightAsync();
                    await Task.Delay(100); // 短暂延时确保绿灯完全关闭
                    await warningLightService.ShowRedLightAsync();
                    package.SetError($"包裹号无效：{result.Message}");
                    break;

                case 1005:
                    // 非履约日包裹
                    Log.Warning("非履约日包裹：{Barcode}", package.Barcode);
                    notificationService.ShowWarning("非履约日包裹", "红灯，非履约日包裹");
                    await warningLightService.TurnOffGreenLightAsync();
                    await Task.Delay(100); // 短暂延时确保绿灯完全关闭
                    await warningLightService.ShowRedLightAsync();
                    package.SetError($"非履约日包裹：{result.Message}");
                    break;

                case 1004:
                    // 重量异常
                    Log.Warning("重量异常：{Barcode}, {Message}", package.Barcode, result.Message);
                    notificationService.ShowWarning("重量异常", "红灯，重量异常");
                    await warningLightService.TurnOffGreenLightAsync();
                    await Task.Delay(100); // 短暂延时确保绿灯完全关闭
                    await warningLightService.ShowRedLightAsync();
                    package.SetError($"重量异常：{result.Message}");
                    break;

                case 400:
                    // 客户端请求错误
                    Log.Warning("客户端请求错误：{Message}", result.Message);
                    notificationService.ShowError("请求错误", "红灯，客户端请求错误");
                    await warningLightService.TurnOffGreenLightAsync();
                    await Task.Delay(100); // 短暂延时确保绿灯完全关闭
                    await warningLightService.ShowRedLightAsync();
                    package.SetError($"请求错误：{result.Message}");
                    break;

                case 500:
                    // 服务端异常
                    Log.Error("服务端异常：{Message}", result.Message);
                    notificationService.ShowError("服务异常", "红灯，系统错误");
                    await warningLightService.TurnOffGreenLightAsync();
                    await Task.Delay(100); // 短暂延时确保绿灯完全关闭
                    await warningLightService.ShowRedLightAsync();
                    package.SetError($"服务异常：{result.Message}");
                    break;

                default:
                    // 未知错误
                    Log.Error("未知错误：Code={Code}, Message={Message}", result.Code, result.Message);
                    notificationService.ShowError("未知错误", $"红灯，错误代码：{result.Code}");
                    await warningLightService.TurnOffGreenLightAsync();
                    await Task.Delay(100); // 短暂延时确保绿灯完全关闭
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

            switch (ex)
            {
                // 记录更详细的异常信息
                case HttpRequestException httpEx:
                    Log.Error(ex, "DWS服务HTTP请求异常: {StatusCode}, {Message}", 
                        httpEx.StatusCode, ex.Message);
                    break;
                case TaskCanceledException:
                    Log.Error(ex, "DWS服务请求超时");
                    break;
                case JsonException jsonEx:
                    Log.Error(ex, "DWS服务响应解析异常: {Message}", jsonEx.Message);
                    break;
                default:
                    Log.Error(ex, "DWS服务请求异常: {Type}, {Message}", ex.GetType().Name, ex.Message);
                    break;
            }

            return new DwsResponse { Code = 500, Message = ex.Message };
        }
    }
}