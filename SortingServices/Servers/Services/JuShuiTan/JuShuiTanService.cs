using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Common.Services.Settings;
using Serilog;
using SortingServices.Servers.Models;

namespace SortingServices.Servers.Services.JuShuiTan;

/// <summary>
///     聚水潭服务实现
/// </summary>
public class JuShuiTanService : IJuShuiTanService
{
    private const string ProductionUrl = "https://openapi.jushuitan.com";
    private const string DevelopmentUrl = "https://dev-api.jushuitan.com";
    private const string WeightSendEndpoint = "/open/orders/weight/send/upload";
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;
    private readonly JsonSerializerOptions _jsonOptions;
    private JushuitanSettings _settings;

    public JuShuiTanService(
        HttpClient httpClient,
        ISettingsService settingsService)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
        _settings = _settingsService.LoadSettings<JushuitanSettings>();
        
        // 配置 JSON 序列化选项
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = new SnakeCaseNamingPolicy(),
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <summary>
    ///     下划线命名策略
    /// </summary>
    private class SnakeCaseNamingPolicy : JsonNamingPolicy
    {
        public override string ConvertName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;

            var builder = new StringBuilder();
            builder.Append(char.ToLowerInvariant(name[0]));

            for (var i = 1; i < name.Length; i++)
            {
                if (char.IsUpper(name[i]))
                {
                    builder.Append('_');
                    builder.Append(char.ToLowerInvariant(name[i]));
                }
                else
                {
                    builder.Append(name[i]);
                }
            }

            return builder.ToString();
        }
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
                { "version", "2" },
                { "biz", JsonSerializer.Serialize(requestArray, _jsonOptions) }
            };

            // 计算签名
            var sign = CalculateSign(parameters);
            parameters.Add("sign", sign);

            // 构建请求内容
            var content = new FormUrlEncodedContent(parameters);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");

            Log.Debug("聚水潭API请求URL: {Url}", url);
            Log.Debug("聚水潭API请求内容: {Content}", JsonSerializer.Serialize(parameters, _jsonOptions));

            // 发送请求
            var response = await _httpClient.PostAsync(url, content);
            var responseContent = await response.Content.ReadAsStringAsync();

            Log.Debug("聚水潭API响应: {Response}", responseContent);

            if (!response.IsSuccessStatusCode)
            {
                Log.Error("聚水潭API请求失败: {StatusCode}, {Response}",
                    response.StatusCode, responseContent);
                throw new HttpRequestException($"API request failed with status code: {response.StatusCode}");
            }

            var result = JsonSerializer.Deserialize<WeightSendResponse>(responseContent, _jsonOptions) ?? throw new JsonException("Failed to deserialize response");
            if (result.Code != 0) Log.Warning("聚水潭API返回错误: {Code}, {Message}", result.Code, result.Message);

            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "调用聚水潭称重发货API时发生错误");
            throw;
        }
    }

    private string CalculateSign(IDictionary<string, string> parameters)
    {
        try
        {
            // 按参数名升序排序
            var sortedParams = new SortedDictionary<string, string>(parameters);

            // 构建签名字符串
            var signBuilder = new StringBuilder();
            foreach (var param in sortedParams.Where(static param => !string.IsNullOrEmpty(param.Value) && param.Key != "sign"))
            {
                signBuilder.Append(param.Key).Append(param.Value);
            }

            // 添加app_secret到开头
            var resultStr = _settings.AppSecret + signBuilder;

            // 计算MD5
            var inputBytes = Encoding.UTF8.GetBytes(resultStr);
            var hashBytes = MD5.HashData(inputBytes);

            // 转换为32位小写十六进制字符串
            var sb = new StringBuilder();
            foreach (var b in hashBytes) sb.Append(b.ToString("x2"));

            Log.Debug("聚水潭API签名原文: {SignString}", resultStr);
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