using System.Net.Http;
using System.Text;
using System.Text.Json;
using Common.Models.Package;
using Common.Services.Settings;
using Presentation_Modules.Models;
using Serilog;
using System.Text.Json.Serialization;

namespace Presentation_Modules.Services;

/// <summary>
///     格口映射服务，负责与服务器通信获取格口号
/// </summary>
public class ChuteMappingService : IDisposable
{
    private const string ApiUrl = "http://123.56.22.107:28081/api/DWSInfo";
    private readonly ModuleConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;
    private string _siteCode;

    public ChuteMappingService(HttpClient httpClient, ISettingsService settingsService)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
        _config = settingsService.LoadSettings<ModuleConfig>();
        _siteCode = _config.SiteCode;

        // 设置默认请求头
        _httpClient.DefaultRequestHeaders.Add("equickToken", _config.Token);

        // 订阅配置更改事件
        _settingsService.OnSettingsChanged<ModuleConfig>(OnConfigChanged);
        
        Log.Information("格口映射服务已初始化，站点代码: {SiteCode}", _siteCode);
    }

    /// <summary>
    ///     处理配置更改事件
    /// </summary>
    private void OnConfigChanged(ModuleConfig newConfig)
    {
        _siteCode = newConfig.SiteCode;
        
        // 更新请求头中的Token
        if (_httpClient.DefaultRequestHeaders.Contains("equickToken"))
        {
            _httpClient.DefaultRequestHeaders.Remove("equickToken");
        }
        _httpClient.DefaultRequestHeaders.Add("equickToken", newConfig.Token);
        
        Log.Information("格口映射服务配置已更新，站点代码: {SiteCode}", _siteCode);
    }

    /// <summary>
    ///     从服务器获取格口号
    /// </summary>
    /// <param name="package">包裹信息</param>
    /// <returns>格口号，如果获取失败则返回null</returns>
    public async Task<int?> GetChuteNumberAsync(PackageInfo package)
    {
        if (string.IsNullOrEmpty(package.Barcode) || package.Barcode.Equals("NoRead", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("包裹条码为空或为NoRead: {Barcode}", package.Barcode);
            return _config.ExceptionChute;
        }
        try
        {
            // 根据站点代码确定handlers
            var handlers = _siteCode == "1002" ? "深圳收货组03" : "上海收货组03";

            // 构建请求数据
            var requestData = new
            {
                packageCode = package.Barcode,
                scanTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                handlers,
                weight = package.Weight.ToString("0.000"),
                siteCode = _siteCode
            };

            // 序列化为JSON
            var content = new StringContent(
                JsonSerializer.Serialize(requestData),
                Encoding.UTF8,
                "application/json");

            // 设置超时时间
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(_config.ServerTimeout));

            // 发送请求
            Log.Information("正在请求格口号: {Barcode}", package.Barcode);
            var response = await _httpClient.PostAsync(ApiUrl, content, cts.Token);

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
                package.Barcode, _config.ServerTimeout);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "获取格口号时发生异常: {Barcode}", package.Barcode);
            return null;
        }
    }

    /// <summary>
    ///     设置站点代码
    /// </summary>
    /// <param name="siteCode">站点代码，1001-上海，1002-深圳</param>
    public void SetSiteCode(string siteCode)
    {
        if (siteCode != "1001" && siteCode != "1002")
            throw new ArgumentException("站点代码无效，只能是1001(上海)或1002(深圳)", nameof(siteCode));

        _siteCode = siteCode;
        Log.Information("已设置站点代码: {SiteCode}", siteCode);
    }

    /// <summary>
    ///     格口响应数据结构
    /// </summary>
    private class ChuteResponse
    {
        [JsonPropertyName("result")]
        public string? Result { get; init; }
        
        [JsonPropertyName("code")]
        public int Code { get; init; }
        
        [JsonPropertyName("msg")]
        public string? Msg { get; init; }
        
        [JsonPropertyName("chute_code")]
        public string? ChuteCode { get; init; }
    }

    public void Dispose()
    {
        _settingsService.OnSettingsChanged<ModuleConfig>(null);
        GC.SuppressFinalize(this);
    }
}