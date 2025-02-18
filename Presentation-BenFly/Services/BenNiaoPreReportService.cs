using System.Net.Http;
using System.Net.Http.Json;
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

            var result = await response.Content.ReadFromJsonAsync<BenNiaoResponse<List<PreReportDataResponse>>>();

            if (result is { IsSuccess: true, Result: not null })
            {
                _preReportData = result.Result;

                Log.Information("成功获取笨鸟预报数据，数量：{Count}", _preReportData.Count);
                if (_preReportData.Count != 0) Log.Information("笨鸟预报数据示例：{@FirstItem}", _preReportData.First());
            }
            else
            {
                var message = result?.Message ?? "未知错误";
                Log.Error("获取笨鸟预报数据失败：{Message}", message);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "获取笨鸟预报数据时发生错误");
        }
    }
}