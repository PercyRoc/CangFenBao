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
public class BenNiaoPreReportService : IDisposable
{
    private const string SettingsKey = "UploadSettings";
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;
    private readonly System.Timers.Timer _updateTimer;
    private List<PreReportDataResponse>? _preReportData;
    private bool _disposed;

    public BenNiaoPreReportService(
        IHttpClientFactory httpClientFactory,
        ISettingsService settingsService)
    {
        _httpClient = httpClientFactory.CreateClient("BenNiao");
        _settingsService = settingsService;

        // 初始化定时器
        _updateTimer = new System.Timers.Timer();
        _updateTimer.Elapsed += async (_, _) => await UpdatePreReportDataAsync();

        // 立即执行一次预报数据更新
        Task.Run(async () =>
        {
            try
            {
                Log.Information("启动时执行预报数据更新");
                await UpdatePreReportDataAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "启动时更新预报数据失败");
            }
        });

        // 启动定时更新
        StartUpdateTimer();
    }

    /// <summary>
    ///     获取预报数据
    /// </summary>
    public List<PreReportDataResponse>? GetPreReportData()
    {
        return _preReportData;
    }

    /// <summary>
    ///     启动定时更新
    /// </summary>
    private void StartUpdateTimer()
    {
        try
        {
            var config = _settingsService.LoadSettings<UploadConfiguration>(SettingsKey);
            var interval = TimeSpan.FromSeconds(config.PreReportUpdateIntervalSeconds);
            
            _updateTimer.Interval = interval.TotalMilliseconds;
            _updateTimer.Start();
            
            Log.Information("已启动预报数据定时更新，间隔：{Interval}秒", config.PreReportUpdateIntervalSeconds);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动预报数据定时更新失败");
        }
    }

    /// <summary>
    ///     更新预报数据
    /// </summary>
    public async Task UpdatePreReportDataAsync()
    {
        try
        {
            Log.Information("开始获取笨鸟预报数据");

            var config = _settingsService.LoadSettings<UploadConfiguration>(SettingsKey);
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

    public void Dispose()
    {
        if (_disposed) return;
        
        _updateTimer.Stop();
        _updateTimer.Dispose();
        _disposed = true;
        
        GC.SuppressFinalize(this);
    }
}