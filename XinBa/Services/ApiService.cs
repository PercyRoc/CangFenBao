using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Common.Services.Settings;
using Common.Services.Ui;
using Serilog;
using XinBa.Services.Models;

namespace XinBa.Services;

/// <summary>
///     API服务接口
/// </summary>
public interface IApiService
{
    /// <summary>
    ///     提交商品尺寸
    /// </summary>
    /// <param name="goodsSticker">商品条码</param>
    /// <param name="height">高度(毫米)</param>
    /// <param name="length">长度(毫米)</param>
    /// <param name="width">宽度(毫米)</param>
    /// <param name="weight">重量(毫克)</param>
    /// <param name="photoData">商品图片数据列表（可选）</param>
    /// <returns>提交是否成功</returns>
    Task<bool> SubmitDimensionsAsync(
        string goodsSticker,
        int height,
        int length,
        int width,
        int weight,
        IEnumerable<byte[]>? photoData = null);
}

/// <summary>
///     API服务实现
/// </summary>
public class ApiService : IApiService
{
    // API认证信息
    private const string DefaultApiUsername = "test_dws";
    private const string DefaultApiPassword = "testWds";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private readonly HttpClient _httpClient;

    private readonly INotificationService _notificationService;

    /// <summary>
    ///     构造函数
    /// </summary>
    public ApiService(ISettingsService settingsService,
        INotificationService notificationService)
    {
        _notificationService = notificationService;

        // 配置HttpClient以处理SSL
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = static (_, _, _, _) => true
        };
        _httpClient = new HttpClient(handler);

        // 设置基础URL
        var apiCredentials = settingsService.LoadSettings<ApiCredentials>();
        var baseUrl = apiCredentials.BaseUrl.TrimEnd('/'); // 确保没有尾部斜杠
        _httpClient.BaseAddress = new Uri(baseUrl + "/"); // 确保有尾部斜杠
        Log.Debug("API基础URL设置为: {BaseUrl}", _httpClient.BaseAddress);

        // 设置默认基本认证
        SetBasicAuthentication(DefaultApiUsername, DefaultApiPassword);

        // 设置超时时间
        _httpClient.Timeout = TimeSpan.FromSeconds(30);

        // 设置请求头
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }



    /// <inheritdoc />
    public async Task<bool> SubmitDimensionsAsync(
        string goodsSticker,
        int height,
        int length,
        int width,
        int weight,
        IEnumerable<byte[]>? photoData = null)
    {
        try
        {
            Log.Information("尝试提交商品尺寸: GoodsSticker={GoodsSticker}", goodsSticker);

            using var formData = new MultipartFormDataContent();
            // 添加基本数据
            formData.Add(new StringContent(goodsSticker), "goods_sticker");
            formData.Add(new StringContent(height.ToString()), "height");
            formData.Add(new StringContent(length.ToString()), "length");
            formData.Add(new StringContent(width.ToString()), "width");
            formData.Add(new StringContent(weight.ToString()), "weight");

            // 添加图片数据
            if (photoData != null)
                foreach (var fileContent in photoData.Select(static imageBytes => new ByteArrayContent(imageBytes)))
                {
                    fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                    formData.Add(fileContent, "photos", $"image_{DateTime.Now:yyyyMMddHHmmss}.jpg");
                }

            const string path = "api/v1/dimensions"; // 移除开头的斜杠
            // 记录请求详情
            Log.Debug("提交尺寸请求详情: BaseUrl={BaseUrl}, Endpoint={Endpoint}", _httpClient.BaseAddress, path);

            var response = await _httpClient.PostAsync(path, formData);
            var responseContent = await response.Content.ReadAsStringAsync();

            Log.Information("服务器响应: StatusCode={StatusCode}, Content={Content}",
                response.StatusCode, responseContent);

            if (response.IsSuccessStatusCode)
            {
                Log.Information("商品尺寸提交成功: GoodsSticker={GoodsSticker}", goodsSticker);
                return true;
            }

            // 处理错误响应
            Log.Error("商品尺寸提交失败: StatusCode={StatusCode}, Response={Response}",
                response.StatusCode, responseContent);

            try
            {
                var errorResponse = JsonSerializer.Deserialize<ErrorResponse>(responseContent, JsonOptions);
                if (!string.IsNullOrEmpty(errorResponse?.message))
                {
                    var errorMessage = errorResponse.message;
                    _notificationService.ShowError($"Submit failed: {errorMessage}");
                    Log.Error("错误详情: {ErrorMessage}", errorMessage);
                }
                else
                {
                    _notificationService.ShowError($"提交失败: HTTP {(int)response.StatusCode} - {response.StatusCode}");
                }
            }
            catch (JsonException ex)
            {
                Log.Error(ex, "解析错误响应失败");
                _notificationService.ShowError("提交失败: 服务器返回了无效的响应格式");
            }

            return false;
        }
        catch (HttpRequestException ex)
        {
            Log.Error(ex, "发送HTTP请求时发生错误");
            _notificationService.ShowError($"网络错误: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "提交商品尺寸过程中发生错误");
            _notificationService.ShowError($"提交过程中发生错误: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    ///     设置HTTP基本认证
    /// </summary>
    private void SetBasicAuthentication(string username, string password)
    {
        var authValue = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authValue);
    }
}