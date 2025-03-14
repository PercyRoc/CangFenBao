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
    ///     员工登录
    /// </summary>
    /// <param name="employeeId">员工ID</param>
    /// <returns>登录是否成功</returns>
    Task<bool> LoginAsync(int employeeId);

    /// <summary>
    ///     员工登录（带凭证）
    /// </summary>
    /// <param name="employeeId">员工ID</param>
    /// <param name="username">API用户名</param>
    /// <param name="password">API密码</param>
    /// <returns>登录是否成功</returns>
    Task<bool> LoginAsync(int employeeId, string username, string password);

    /// <summary>
    ///     员工登出
    /// </summary>
    /// <returns>登出是否成功</returns>
    Task<bool> LogoutAsync();

    /// <summary>
    ///     获取当前登录的员工ID
    /// </summary>
    /// <returns>员工ID，如果未登录则返回null</returns>
    int? GetCurrentEmployeeId();

    /// <summary>
    ///     检查是否已登录
    /// </summary>
    /// <returns>是否已登录</returns>
    bool IsLoggedIn();

    /// <summary>
    ///     提交商品尺寸
    /// </summary>
    /// <param name="goodsSticker">商品条码</param>
    /// <param name="height">高度(cm)</param>
    /// <param name="length">长度(cm)</param>
    /// <param name="width">宽度(cm)</param>
    /// <param name="weight">重量(g)</param>
    /// <param name="photoData">商品图片数据列表（可选）</param>
    /// <returns>提交是否成功</returns>
    Task<bool> SubmitDimensionsAsync(
        string goodsSticker,
        string height,
        string length,
        string width,
        string weight,
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
    private readonly HttpClient _httpClient;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;

    /// <summary>
    ///     构造函数
    /// </summary>
    public ApiService(ISettingsService settingsService,
        INotificationService notificationService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;

        // 配置HttpClient以处理SSL
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = static (_, _, _, _) => true
        };
        _httpClient = new HttpClient(handler);

        // 设置基础URL
        var apiCredentials = _settingsService.LoadSettings<ApiCredentials>();
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
    public Task<bool> LoginAsync(int employeeId)
    {
        // 使用默认凭证登录
        return LoginAsync(employeeId, DefaultApiUsername, DefaultApiPassword);
    }

    /// <inheritdoc />
    public async Task<bool> LoginAsync(int employeeId, string username, string password)
    {
        try
        {
            Log.Information("尝试员工登录: EmployeeId={EmployeeId}, Username={Username}", employeeId, username);

            // 设置认证信息
            SetBasicAuthentication(username, password);

            var request = new EmployeeLoginRequest
            {
                Eid = employeeId
            };

            var content = new StringContent(
                JsonSerializer.Serialize(request, _jsonOptions),
                Encoding.UTF8,
                "application/json");

            const string path = "api/v1/employee/login"; // 移除开头的斜杠

            // 记录完整的请求URL和认证信息
            Log.Debug("请求URL: {BaseUrl}{Path}", _httpClient.BaseAddress, path);
            Log.Debug("认证信息: {Auth}", _httpClient.DefaultRequestHeaders.Authorization?.ToString());
            Log.Debug("请求内容: {Content}", await content.ReadAsStringAsync());

            var response = await _httpClient.PostAsync(path, content);

            if (response.IsSuccessStatusCode)
            {
                // 保存登录状态
                var settings = new EmployeeSettings
                {
                    EmployeeId = employeeId,
                    IsLoggedIn = true,
                    LoginTime = DateTime.Now
                };

                _settingsService.SaveSettings(settings);

                // 保存API凭证
                var apiCredentials = _settingsService.LoadSettings<ApiCredentials>();
                apiCredentials.Username = username;
                apiCredentials.Password = password;
                _settingsService.SaveSettings(apiCredentials);

                Log.Information("员工登录成功: EmployeeId={EmployeeId}", employeeId);
                return true;
            }

            // 处理错误响应
            var errorContent = await response.Content.ReadAsStringAsync();
            Log.Error("员工登录失败: StatusCode={StatusCode}, Response={Response}",
                response.StatusCode, errorContent);

            try
            {
                var errorResponse = JsonSerializer.Deserialize<ErrorResponse>(errorContent, _jsonOptions);
                _notificationService.ShowError(errorResponse != null
                    ? $"登录失败: {errorResponse.Message}"
                    : $"登录失败: {response.StatusCode}");
            }
            catch
            {
                _notificationService.ShowError($"登录失败: {response.StatusCode}");
            }

            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "员工登录过程中发生错误");
            _notificationService.ShowError($"登录过程中发生错误: {ex.Message}");
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> LogoutAsync()
    {
        try
        {
            var settings = _settingsService.LoadSettings<EmployeeSettings>();
            if (!settings.IsLoggedIn)
            {
                Log.Warning("尝试登出但当前没有登录的员工");
                return true;
            }

            Log.Information("尝试员工登出: EmployeeId={EmployeeId}", settings.EmployeeId);

            var request = new EmployeeLoginRequest
            {
                Eid = settings.EmployeeId
            };

            var content = new StringContent(
                JsonSerializer.Serialize(request, _jsonOptions),
                Encoding.UTF8,
                "application/json");

            const string path = "api/v1/employee/logout"; // 移除开头的斜杠
            var response = await _httpClient.PostAsync(path, content);

            // 无论API响应如何，都清除本地登录状态
            settings.IsLoggedIn = false;
            settings.LoginTime = null;
            _settingsService.SaveSettings(settings);

            if (response.IsSuccessStatusCode)
            {
                Log.Information("员工登出成功: EmployeeId={EmployeeId}", settings.EmployeeId);
                return true;
            }

            // 处理错误响应
            var errorContent = await response.Content.ReadAsStringAsync();
            Log.Error("员工登出失败: StatusCode={StatusCode}, Response={Response}",
                response.StatusCode, errorContent);

            try
            {
                var errorResponse = JsonSerializer.Deserialize<ErrorResponse>(errorContent, _jsonOptions);
                _notificationService.ShowError(errorResponse != null
                    ? $"登出失败: {errorResponse.Message}"
                    : $"登出失败: {response.StatusCode}");
            }
            catch
            {
                _notificationService.ShowError($"登出失败: {response.StatusCode}");
            }

            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "员工登出过程中发生错误");
            _notificationService.ShowError($"登出过程中发生错误: {ex.Message}");
            return false;
        }
    }

    /// <inheritdoc />
    public int? GetCurrentEmployeeId()
    {
        var settings = _settingsService.LoadSettings<EmployeeSettings>();
        return settings.IsLoggedIn ? settings.EmployeeId : null;
    }

    /// <inheritdoc />
    public bool IsLoggedIn()
    {
        var settings = _settingsService.LoadSettings<EmployeeSettings>();
        return settings.IsLoggedIn;
    }

    /// <inheritdoc />
    public async Task<bool> SubmitDimensionsAsync(
        string goodsSticker,
        string height,
        string length,
        string width,
        string weight,
        IEnumerable<byte[]>? photoData = null)
    {
        try
        {
            Log.Information("尝试提交商品尺寸: GoodsSticker={GoodsSticker}", goodsSticker);

            using var formData = new MultipartFormDataContent();
            // 添加基本数据
            formData.Add(new StringContent(goodsSticker), "goods_sticker");
            formData.Add(new StringContent(height), "height");
            formData.Add(new StringContent(length), "length");
            formData.Add(new StringContent(width), "width");
            formData.Add(new StringContent(weight), "weight");

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
                var errorResponse = JsonSerializer.Deserialize<ErrorResponse>(responseContent, _jsonOptions);
                if (errorResponse != null)
                {
                    _notificationService.ShowError($"提交失败: {errorResponse.Message}");
                    Log.Error("错误详情: {ErrorMessage}", errorResponse.Message);
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