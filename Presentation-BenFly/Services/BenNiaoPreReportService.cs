using System.Net.Http;
using System.Text;
using System.Text.Json;
using CommonLibrary.Services;
using Presentation_BenFly.Models.BenNiao;
using Presentation_BenFly.Models.Upload;
using Serilog;

namespace Presentation_BenFly.Services;

/// <summary>
///     笨鸟预报数据服务
/// </summary>
public class BenNiaoPreReportService(
    IHttpClientFactory httpClientFactory,
    ISettingsService settingsService)
{
    private const string SettingsKey = "UploadSettings";
    private readonly HttpClient _httpClient = httpClientFactory.CreateClient("BenNiao");
    private DateTime _lastUpdateTime;
    private List<PreReportDataResponse>? _preReportData;

    /// <summary>
    ///     获取预报数据
    /// </summary>
    public List<PreReportDataResponse>? GetPreReportData()
    {
        return _preReportData;
    }

    /// <summary>
    ///     更新预报数据
    /// </summary>
    public async Task UpdatePreReportDataAsync()
    {
        try
        {
            Log.Information("开始获取笨鸟预报数据");

            var config = settingsService.LoadSettings<UploadConfiguration>(SettingsKey);
            const string url = "/api/openApi/dataDownload";

            // 构建请求参数
            var requestBody = new { netWorkName = config.BenNiaoDistributionCenterName };
            Log.Information("笨鸟预报数据请求参数：{@RequestBody}", requestBody);

            // 创建签名请求
            var request = BenNiaoSignHelper.CreateRequest(
                config.BenNiaoAppId,
                config.BenNiaoAppSecret,
                requestBody);

            Log.Information("笨鸟预报数据签名请求：{@Request}", JsonSerializer.Serialize(request));
            Log.Information("笨鸟预报数据请求地址：{BaseUrl}{Url}", _httpClient.BaseAddress, url);

            // 发送请求
            var jsonContent = JsonSerializer.Serialize(request);
            Log.Information("笨鸟预报数据请求JSON：{@JsonContent}", jsonContent);

            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content);

            Log.Information("笨鸟预报数据响应状态码：{StatusCode}", response.StatusCode);

            var responseContent = await response.Content.ReadAsStringAsync();
            Log.Information("笨鸟预报数据响应内容：{@Response}", responseContent);

            response.EnsureSuccessStatusCode();

            // 使用 JsonDocument 解析响应
            using var document = JsonDocument.Parse(responseContent);
            var root = document.RootElement;

            if (root.TryGetProperty("success", out var success) && success.GetBoolean())
            {
                var resultArray = root.GetProperty("result");
                var newData = new List<PreReportDataResponse>();

                foreach (var item in resultArray.EnumerateArray())
                    try
                    {
                        var preReportData = PreReportDataResponse.FromArray(item);
                        newData.Add(preReportData);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "解析预报数据项时发生错误：{@Item}", item);
                    }

                _preReportData = newData;
                _lastUpdateTime = DateTime.Now;

                Log.Information("成功获取笨鸟预报数据，数量：{Count}", _preReportData.Count);
                if (_preReportData.Count != 0) Log.Information("笨鸟预报数据示例：{@FirstItem}", _preReportData.First());
            }
            else
            {
                var message = root.TryGetProperty("message", out var msg) ? msg.GetString() : "未知错误";
                Log.Error("获取笨鸟预报数据失败：{Message}", message);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "获取笨鸟预报数据时发生错误");
        }
    }

    /// <summary>
    ///     获取最后更新时间
    /// </summary>
    public DateTime GetLastUpdateTime()
    {
        return _lastUpdateTime;
    }
}