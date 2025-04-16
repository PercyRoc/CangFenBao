using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BenFly.Models.BenNiao;
using Common.Services.Settings;
using BenFly.Models.Upload;
using Serilog;
using Timer = System.Timers.Timer;

namespace BenFly.Services;

/// <summary>
///     笨鸟预报数据服务
/// </summary>
internal class BenNiaoPreReportService : IDisposable
{
    private const string SettingsKey = "UploadSettings";
    private readonly IHttpClientFactory _httpClientFactory;

    // 创建JSON序列化选项，避免中文转义
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    private readonly Timer _updateTimer;
    private UploadConfiguration _config;
    private bool _disposed;
    private HttpClient _httpClient;
    private List<PreReportDataResponse>? _preReportData;

    public BenNiaoPreReportService(
        IHttpClientFactory httpClientFactory,
        ISettingsService settingsService)
    {
        _httpClientFactory = httpClientFactory;
        _config = settingsService.LoadSettings<UploadConfiguration>(SettingsKey);

        // 初始化 HttpClient
        _httpClient = CreateHttpClient();

        // 初始化定时器
        _updateTimer = new Timer();
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

    public void Dispose()
    {
        if (_disposed) return;

        _updateTimer.Stop();
        _updateTimer.Dispose();
        _disposed = true;

        GC.SuppressFinalize(this);
    }

    private HttpClient CreateHttpClient()
    {
        var baseUrl = _config.BenNiaoEnvironment == BenNiaoEnvironment.Production
            ? "http://bnsy.benniaosuyun.com"
            : "http://sit.bnsy.rhb56.cn";

        var client = _httpClientFactory.CreateClient("BenNiao");
        client.BaseAddress = new Uri(baseUrl);
        Log.Information("已创建 HttpClient，BaseUrl: {BaseUrl}", baseUrl);
        return client;
    }

    /// <summary>
    ///     获取预报数据
    /// </summary>
    internal IEnumerable<PreReportDataResponse>? GetPreReportData()
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
            var interval = TimeSpan.FromSeconds(_config.PreReportUpdateIntervalSeconds);

            _updateTimer.Interval = interval.TotalMilliseconds;
            _updateTimer.Start();

            Log.Information("已启动预报数据定时更新，间隔：{Interval}秒", _config.PreReportUpdateIntervalSeconds);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动预报数据定时更新失败");
        }
    }

    /// <summary>
    ///     更新预报数据
    /// </summary>
    private async Task UpdatePreReportDataAsync()
    {
        try
        {

            const string url = "/api/openApi/dataDownload";

            // 构建请求参数
            var requestBody = new { netWorkName = _config.BenNiaoDistributionCenterName };

            // 创建签名请求
            var request = BenNiaoSignHelper.CreateRequest(
                _config.BenNiaoAppId,
                _config.BenNiaoAppSecret,
                requestBody);
            // 发送请求
            var jsonContent = JsonSerializer.Serialize(request, _jsonOptions);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content);
            response.EnsureSuccessStatusCode();

            var result =
                await response.Content.ReadFromJsonAsync<BenNiaoResponse<List<PreReportDataResponse>>>(_jsonOptions);

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