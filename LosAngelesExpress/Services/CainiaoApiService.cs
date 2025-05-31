using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Common.Models.Package;
using Common.Services.Settings;
using LosAngelesExpress.Models.Api;
using LosAngelesExpress.Models.Settings;
using Serilog;

namespace LosAngelesExpress.Services;

/// <summary>
/// 洛杉矶菜鸟API服务实现
/// </summary>
public class CainiaoApiService(HttpClient httpClient, ISettingsService settingsService) : ICainiaoApiService
{
    // 缓存 JsonSerializerOptions 实例
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null, // 保持原始字段名
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// 上传包裹信息到菜鸟服务器
    /// </summary>
    public async Task<CainiaoApiUploadResult> UploadPackageAsync(PackageInfo packageInfo)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var settings = settingsService.LoadSettings<CainiaoApiSettings>();
            // 构建 logistics_interface 对象
            var logistics = new CainiaoOpenLogisticsInterface
            {
                BizCode = packageInfo.Barcode,
                Weight = (int)Math.Round(packageInfo.Weight * 1000), // g
                Height = (int)Math.Round(packageInfo.Height * 10 ?? 0), // cm
                Length = (int)Math.Round(packageInfo.Length * 10 ?? 0), // cm
                Width = (int)Math.Round(packageInfo.Width * 10 ?? 0), // cm
                WarehouseCode = "TRAN_STORE_30867964",
                Workbench = settings.Workbench ?? "",
                WeightUnit = "g",
                DimensionUnit = "mm"
            };
            var logisticsJson = JsonSerializer.Serialize(logistics, JsonOptions);
            Log.Information(
                "[菜鸟API] 上传包裹请求参数: logistics_interface={LogisticsJson}, BizCode={BizCode}, Weight={Weight}, Height={Height}, Length={Length}, Width={Width}, WarehouseCode={WarehouseCode}, Workbench={Workbench}",
                logisticsJson, logistics.BizCode, logistics.Weight, logistics.Height, logistics.Length, logistics.Width,
                logistics.WarehouseCode, logistics.Workbench);
            // 生成签名
            var dataDigest = GenerateDataDigest(logisticsJson, settings.AppSecret);
            // 构建表单参数
            var form = new List<KeyValuePair<string, string>>
            {
                new("logistics_interface", logisticsJson),
                new("msg_type", "GLOBAL_SMART_SITE_SIGN_IN_NOTIFY"),
                new("logistic_provider_id", "wuke_iot"),
                new("data_digest", dataDigest),
                new("to_code", "smart_site_am")
            };
            var content = new FormUrlEncodedContent(form);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            var response = await httpClient.PostAsync(settings.CurrentApiUrl, content, cts.Token);
            stopwatch.Stop();
            var responseJson = await response.Content.ReadAsStringAsync(cts.Token);
            Log.Information("[菜鸟API] 上传包裹响应: StatusCode={StatusCode}, Response={Response}, 耗时={ElapsedMs}ms",
                response.StatusCode, responseJson, stopwatch.ElapsedMilliseconds);
            return response.IsSuccessStatusCode
                ? CainiaoApiUploadResult.Success(null, stopwatch.ElapsedMilliseconds)
                : CainiaoApiUploadResult.Failure($"HTTP {response.StatusCode}: {responseJson}",
                    (int)response.StatusCode, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Log.Error(ex, "[菜鸟API] 上传包裹发生异常: {Message}, 耗时={ElapsedMs}ms", ex.Message, stopwatch.ElapsedMilliseconds);
            return CainiaoApiUploadResult.Failure($"Unexpected error: {ex.Message}",
                responseTimeMs: stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// 检查服务连接状态
    /// </summary>
    public async Task<bool> CheckConnectionAsync()
    {
        try
        {
            var settings = settingsService.LoadSettings<CainiaoApiSettings>();
            var response = await httpClient.GetAsync(settings.CurrentApiUrl);
            return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed;
        }
        catch
        {
            return false;
        }
    }

    #region Private Methods

    private static string GenerateDataDigest(string logisticsInterface, string secretKey)
    {
        var signContent = logisticsInterface + secretKey;
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(signContent));
        return Convert.ToBase64String(hash);
    }

    #endregion
}