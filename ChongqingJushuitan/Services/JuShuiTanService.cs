using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChongqingJushuitan.Models.JuShuiTan;
using ChongqingJushuitan.ViewModels.Settings;
using Common.Services.Settings;
using Serilog;

namespace ChongqingJushuitan.Services;

/// <summary>
///     聚水潭服务实现
/// </summary>
internal class JuShuiTanService : IJuShuiTanService
{
    private const string ProductionUrl = "https://openapi.jushuitan.com";
    private const string DevelopmentUrl = "https://dev-api.jushuitan.com";
    private const string WeightSendEndpoint = "/open/orders/weight/send/upload";
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;
    private JushuitanSettings _settings;

    public JuShuiTanService(
        HttpClient httpClient,
        ISettingsService settingsService)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
        _settings = _settingsService.LoadSettings<JushuitanSettings>();
    }

    /// <inheritdoc />
    public Task<WeightSendResponse> WeightAndSendAsync(WeightSendRequest request)
    {
        return BatchWeightAndSendAsync([request]);
    }

    /// <inheritdoc />
    public async Task<WeightSendResponse> BatchWeightAndSendAsync(IEnumerable<WeightSendRequest> requests)
    {
        try
        {
            // 刷新设置
            _settings = _settingsService.LoadSettings<JushuitanSettings>();

            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var requestArray = requests.ToArray();

            // 构建请求URL
            var baseUrl = _settings.IsProduction ? ProductionUrl : DevelopmentUrl;
            var url = $"{baseUrl}{WeightSendEndpoint}";

            // 构建请求参数
            var parameters = new Dictionary<string, string>
            {
                { "app_key", _settings.AppKey },
                { "access_token", _settings.AccessToken },
                { "timestamp", timestamp.ToString() },
                { "charset", "utf-8" },
                { "version", "2" }
            };

            // 计算签名
            var sign = CalculateSign(parameters, requestArray);
            parameters.Add("sign", sign);

            // 构建请求内容
            var content = new StringContent(
                JsonSerializer.Serialize(requestArray),
                Encoding.UTF8,
                "application/json");

            // 添加查询参数
            var query = string.Join("&", parameters.Select(static p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
            var requestUrl = $"{url}?{query}";

            Log.Debug("聚水潭API请求URL: {Url}", requestUrl);
            Log.Debug("聚水潭API请求内容: {Content}", JsonSerializer.Serialize(requestArray));

            // 发送请求
            var response = await _httpClient.PostAsync(requestUrl, content);
            var responseContent = await response.Content.ReadAsStringAsync();

            Log.Debug("聚水潭API响应: {Response}", responseContent);

            if (!response.IsSuccessStatusCode)
            {
                Log.Error("聚水潭API请求失败: {StatusCode}, {Response}",
                    response.StatusCode, responseContent);
                throw new HttpRequestException($"API request failed with status code: {response.StatusCode}");
            }

            var result = JsonSerializer.Deserialize<WeightSendResponse>(responseContent);
            if (result == null) throw new JsonException("Failed to deserialize response");

            if (!result.IsSuccess) Log.Warning("聚水潭API返回错误: {Code}, {Message}", result.Code, result.Message);

            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "调用聚水潭称重发货API时发生错误");
            throw;
        }
    }

    private string CalculateSign(IDictionary<string, string> parameters, WeightSendRequest[] requests)
    {
        try
        {
            // 按参数名升序排序
            var sortedParams = new SortedDictionary<string, string>(parameters);

            // 构建签名字符串
            var signBuilder = new StringBuilder();
            foreach (var param in sortedParams.Where(static param => !string.IsNullOrEmpty(param.Value)))
                signBuilder.Append(param.Key).Append(param.Value);

            // 添加请求体
            var requestBody = JsonSerializer.Serialize(requests);
            signBuilder.Append(requestBody);

            // 添加app_secret
            signBuilder.Append(_settings.AppSecret);

            // 计算MD5
            var inputBytes = Encoding.UTF8.GetBytes(signBuilder.ToString());
            var hashBytes = MD5.HashData(inputBytes);

            // 转换为32位小写十六进制字符串
            var sb = new StringBuilder();
            foreach (var b in hashBytes) sb.Append(b.ToString("x2"));

            Log.Debug("聚水潭API签名原文: {SignString}", signBuilder.ToString());
            Log.Debug("聚水潭API签名结果: {Sign}", sb.ToString());

            return sb.ToString();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "计算聚水潭API签名时发生错误");
            throw;
        }
    }
}