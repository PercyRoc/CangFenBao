using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Common.Models.Package;
using Common.Services.Settings;
using Serilog;
using ShanghaiModuleBelt.Models;

namespace ShanghaiModuleBelt.Services;

/// <summary>
///     格口映射服务，负责与服务器通信获取格口号
/// </summary>
public class ChuteMappingService(HttpClient httpClient, ISettingsService settingsService) : IDisposable
{
    private const string ApiUrl = "http://123.56.22.107:28081/api/DWSInfo";

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     从服务器获取格口号
    /// </summary>
    /// <param name="package">包裹信息</param>
    /// <returns>格口号，如果获取失败则返回null</returns>
    internal async Task<int?> GetChuteNumberAsync(PackageInfo package)
    {
        // 每次请求时加载最新配置
        var config = settingsService.LoadSettings<ModuleConfig>();

        if (string.IsNullOrEmpty(package.Barcode) ||
            package.Barcode.Equals("NoRead", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("包裹条码为空或为NoRead: {Barcode}", package.Barcode);
            return config.ExceptionChute;
        }

        try
        {
            // 根据站点代码确定handlers
            const string handlers = "上海收货组08";

            // 构建请求数据
            var requestData = new
            {
                packageCode = package.Barcode,
                scanTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                handlers,
                weight = package.Weight.ToString("0.000"),
                siteCode = config.SiteCode
            };

            // 序列化为JSON
            var content = new StringContent(
                JsonSerializer.Serialize(requestData),
                Encoding.UTF8,
                "application/json");

            // 设置超时时间
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(config.ServerTimeout));

            // 发送请求
            Log.Information("正在请求格口号: {Barcode}", package.Barcode);

            // 设置请求头
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            request.Headers.Add("equickToken", config.Token);
            request.Content = content;

            var response = await httpClient.SendAsync(request, cts.Token);

            // 检查响应状态
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("获取格口号失败: HTTP状态码 {StatusCode}", response.StatusCode);
                return null;
            }

            // 解析响应
            var responseJson = await response.Content.ReadAsStringAsync(cts.Token);
            var responseData = JsonSerializer.Deserialize<ChuteResponse>(responseJson);

            if (responseData == null)
            {
                Log.Warning("解析格口号响应失败: {Response}", responseJson);
                return null;
            }

            // 检查响应码
            if (responseData.Code != 200)
            {
                Log.Warning("获取格口号失败: 错误码 {Code}, 消息 {Message}",
                    responseData.Code, responseData.Msg);
                return null;
            }

            // 解析格口号
            if (int.TryParse(responseData.ChuteCode, out var chuteNumber))
            {
                Log.Information("成功获取格口号: {Barcode} -> {ChuteNumber}",
                    package.Barcode, chuteNumber);
                return chuteNumber;
            }

            Log.Warning("格口号格式无效: {ChuteCode}", responseData.ChuteCode);
            return null;
        }
        catch (TaskCanceledException)
        {
            Log.Warning("获取格口号超时: {Barcode}, 超时时间: {Timeout}ms",
                package.Barcode, config.ServerTimeout);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "获取格口号时发生异常: {Barcode}", package.Barcode);
            return null;
        }
    }

    /// <summary>
    ///     格口响应数据结构
    /// </summary>
    private class ChuteResponse
    {
        [JsonPropertyName("result")] public string? Result { get; init; }

        [JsonPropertyName("code")] public int Code { get; init; }

        [JsonPropertyName("msg")] public string? Msg { get; init; }

        [JsonPropertyName("chute_code")] public string? ChuteCode { get; init; }
    }
}